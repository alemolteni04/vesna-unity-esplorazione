using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
 
// ============================================================
// ExplorationManager.cs  — v2.1
// FIX:
//   1. HandleCorridorPoleVisible → sceglie sempre il polo fisicamente
//      più vicino all'agente, non il primo visto dal cono.
//      Così l'agente entra sempre da polo A (il più vicino),
//      non da polo B (il più lontano/visibile).
//   2. UpdateBacktracking → threshold ridotta + guard pathPending
//      per evitare falsi "arrivato" immediatamente dopo SetDestination.
//   3. HandleDoorVisibleInRoom → accettato anche in State.InsideRoom
//      dopo il 360° (già c'era, ma ora è esplicitato nel commento).
// ============================================================
 
public class ExplorationManager : MonoBehaviour
{
    [Header("Agente")]
    public NavMeshAgent          navAgent;
    public ExplorationVisionCone explorationVisionCone;
 
    [Header("Macro-Grafo edificio")]
    public BuildingGraph buildingGraph;
 
    [Header("Piano iniziale")]
    public int startingFloor = 0;
 
    [Header("Stanza di partenza — trascina il GameObject dall'Inspector")]
    [Tooltip("Trascina qui il GameObject della stanza/nodo da cui parte l'agente.\n" +
             "Il suo nome verrà usato come ID del nodo iniziale nel grafo.\n" +
             "NON scrivere stringhe a mano.")]
    public GameObject startRoomObject;
 
    [Header("Impostazioni")]
    public float arrivalThreshold      = 0.5f;
    public float poleArrivalThreshold  = 1.2f; // più largo: i poli possono essere vicino ai muri
    public float rotationSpeed         = 120f;
    public float inspectionDistance    = 1.2f;
    public float enterOffset           = 1.5f;
    public float poleTimeoutSeconds    = 4f;   // se dopo X sec non è arrivato, forza ProcessPoleTouched
 
    // --------------------------------------------------------
    // MACCHINA A STATI
    // --------------------------------------------------------
    private enum State
    {
        Idle,
        MovingToPole,
        Transiting,
        WaitingAtPoleB,
        Rotating360,
        MovingToDoor,
        InspectingDoor,
        EnteringRoom,
        InsideRoom,
        Backtracking,
        MovingToConnector,
        Completed
    }
 
    private State            state               = State.Idle;
    private TopologicalGraph graph               = new TopologicalGraph();
    private string           currentNodeId;
 
    public TopologicalGraph Graph         => graph;
    public string           CurrentNodeId => currentNodeId;
 
    private string  transitCorridorId;
    private Vector3 poleAPos;
    private Vector3 poleBPos;
 
    private string  pendingCorridorId;
    private string  pendingPoleId;
    private Vector3 pendingPolePos;
 
    private GraphEdge currentEdge;
 
    private float rotationAccum = 0f;
    public  bool  inRoomMode   = false;
 
    private HashSet<string> registeredRoomDoors    = new HashSet<string>();
    private HashSet<string> registeredTransitDoors = new HashSet<string>();
 
    private bool explorationStarted = false;
    private int  currentFloor;
    private VerticalConnector pendingConnector;

    // ── tracking polo in volo ────────────────────────────────────────────────
    // Quando il cono vede PIÙ poli dello stesso corridoio nello stesso frame,
    // ricordiamo quello più vicino già scelto per non sovrascriverlo.
    private string movingToPoleCorridorId;
    private float  movingToPoleTimer   = 0f;
    private float  backtrackTimer      = 0f;
    public  float  backtrackTimeout    = 5f;

    // --------------------------------------------------------
    // EVENTI
    // --------------------------------------------------------
    void Awake()
    {
        if (buildingGraph != null)
            foreach (var floor in buildingGraph.floors)
                floor.explored = false;

        CorridorPoleScript.OnPoleTouched            += HandlePoleTouched;
        ExplorationVisionCone.OnDoorDiscovered      += HandleDoorDiscovered;
        ExplorationVisionCone.OnCorridorPoleVisible += HandleCorridorPoleVisible;
        ExplorationVisionCone.OnDoorVisibleInRoom   += HandleDoorVisibleInRoom;
        VerticalConnectorScript.OnConnectorReached  += HandleConnectorReached;
    }
 
    void OnDestroy()
    {
        CorridorPoleScript.OnPoleTouched            -= HandlePoleTouched;
        ExplorationVisionCone.OnDoorDiscovered      -= HandleDoorDiscovered;
        ExplorationVisionCone.OnCorridorPoleVisible -= HandleCorridorPoleVisible;
        ExplorationVisionCone.OnDoorVisibleInRoom   -= HandleDoorVisibleInRoom;
        VerticalConnectorScript.OnConnectorReached  -= HandleConnectorReached;
    }
 
    // --------------------------------------------------------
    // AVVIO
    // --------------------------------------------------------
    void Start()
    {
        if (startRoomObject != null)
            StartExploration(startRoomObject.name, startRoomObject.transform.position);
        else
            Debug.LogWarning("[ExplMgr] startRoomObject non assegnato!");
    }
 
    public void StartExploration(string startNodeId, Vector3 startPos)
    {
        if (explorationStarted) return;
        explorationStarted = true;
        currentFloor = startingFloor;
 
        explorationVisionCone?.SetExplorationMode(true);
        currentNodeId = startNodeId;
        graph.AddRoomNode(startNodeId, startPos);
        graph.EnterNode(startNodeId);
 
        // ri-sottoscrivi (idempotente) solo per sicurezza
        CorridorPoleScript.OnPoleTouched -= HandlePoleTouched;
        CorridorPoleScript.OnPoleTouched += HandlePoleTouched;
 
        StartRotation360(isRoom: true);
        Debug.Log($"[ExplMgr] Avviato da '{startNodeId}' piano {currentFloor}");
    }
 
    // --------------------------------------------------------
    // UPDATE
    // --------------------------------------------------------
    void Update()
    {
        switch (state)
        {
            case State.MovingToPole:      UpdateMovingToPole();      break;
            case State.Transiting:        UpdateTransiting();        break;
            case State.Rotating360:       UpdateRotating360();       break;
            case State.MovingToDoor:      UpdateMovingToDoor();      break;
            case State.InspectingDoor:    UpdateInspectingDoor();    break;
            case State.EnteringRoom:      UpdateEnteringRoom();      break;
            case State.InsideRoom:        UpdateInsideRoom();        break;
            case State.Backtracking:      UpdateBacktracking();      break;
            case State.MovingToConnector: UpdateMovingToConnector(); break;
        }
    }
 
    // --------------------------------------------------------
    // HANDLER: VisionCone vede un polo di corridoio
    //
    // FIX PRINCIPALE: invece di andare al primo polo visto,
    // scegliamo sempre il polo FISICAMENTE PIÙ VICINO all'agente
    // tra tutti i poli dello stesso corridoio.
    // Questo garantisce che si entri sempre da A (il più prossimo),
    // indipendentemente da quale polo il cono veda per primo.
    // --------------------------------------------------------
    private void HandleCorridorPoleVisible(string corridorId, string poleId, Vector3 polePos)
    {
        // Non interrompere transito, avvicinamento o 360° in corso
        if (state == State.MovingToPole || state == State.Transiting) return;
        if (state == State.Rotating360) return;

        // Solo da Idle o Backtracking
        if (state != State.Idle && state != State.Backtracking) return;

        var corridorData = FindCorridorData(corridorId);
        if (corridorData == null) return;

        // ── Trova il polo più vicino tra i due fisici ──────────────────────
        Vector3 myPos = transform.position;
        float distPole1 = Vector3.Distance(myPos, corridorData.Pole1Position);
        float distPole2 = Vector3.Distance(myPos, corridorData.Pole2Position);

        Vector3 nearestPolePos;
        string  nearestPoleId;

        if (distPole1 <= distPole2)
        {
            nearestPolePos = corridorData.Pole1Position;
            nearestPoleId  = "1";
        }
        else
        {
            nearestPolePos = corridorData.Pole2Position;
            nearestPoleId  = "2";
        }

        // Se stiamo già andando verso questo corridoio verso il polo corretto, ignora
        if (movingToPoleCorridorId == corridorId) return;

        pendingCorridorId     = corridorId;
        pendingPoleId         = nearestPoleId;
        pendingPolePos        = nearestPolePos;
        movingToPoleCorridorId = corridorId;

        navAgent.isStopped = false;
        navAgent.SetDestination(nearestPolePos);
        movingToPoleTimer = 0f;
        state = State.MovingToPole;

        Debug.Log($"[ExplMgr] Polo {corridorId}/{nearestPoleId} individuato → mi avvicino " +
                  $"(dist1={distPole1:F1} dist2={distPole2:F1}, scelto polo {nearestPoleId}).");
    }
 
    // --------------------------------------------------------
    // HANDLER: Polo fisicamente toccato (trigger collider)
    // --------------------------------------------------------
    private void HandlePoleTouched(string corridorId, string poleId, Vector3 polePos)
    {
        Debug.Log($"[ExplMgr] Segnale fisico da {corridorId}_{poleId}. Stato: {state}");

        // CASO 1: Polo B durante transito — lo aspettavamo
        if (state == State.Transiting && corridorId == transitCorridorId)
        {
            pendingCorridorId = corridorId;
            pendingPoleId     = poleId;
            pendingPolePos    = polePos;
            ProcessPoleTouched();
            return;
        }

        // CASO 2: Polo A — solo se stiamo ESPLICITAMENTE andando verso di lui
        // (evita che tocchi accidentali durante MovingToDoor/InspectingDoor
        //  avviino un transito inaspettato e interrompano l'ispezione)
        if (state == State.MovingToPole && corridorId == pendingCorridorId)
        {
            pendingPolePos = polePos; // aggiorna posizione reale
            ProcessPoleTouched();
            return;
        }

        // Stato Idle: l'agente è appena entrato in un corridoio attraverso un varco
        // e il polo fisico è stato toccato prima che il VisionCone lo vedesse visivamente.
        // Accettiamo SOLO se il corridoio non è già stato completamente transitato
        // (arco centrale ancora discovered = transito mai completato).
        if (state == State.Idle)
        {
            var poleANode = graph.GetCorridorPoleNode($"{corridorId}_A");
            if (poleANode != null)
            {
                var central = poleANode.edges.Find(e => e?.id == $"central_{corridorId}");
                if (central != null && central.state == EdgeState.Explored)
                {
                    Debug.Log($"[ExplMgr] Polo {corridorId}_{poleId} ignorato in Idle " +
                              $"— corridoio già transitato.");
                    return;
                }
            }
            pendingCorridorId = corridorId;
            pendingPoleId     = poleId;
            pendingPolePos    = polePos;
            ProcessPoleTouched();
            return;
        }

        Debug.Log($"[ExplMgr] Polo {corridorId}_{poleId} ignorato — stato={state} non compatibile.");
    }
 
    // --------------------------------------------------------
    // HANDLER: DoorSensor scopre una porta durante il transito
    // --------------------------------------------------------
    private void HandleDoorDiscovered(DoorVarcoScript door, string corridorId, int orderFW)
    {
        if (state != State.Transiting) return;
        if (corridorId != transitCorridorId) return;
        if (registeredTransitDoors.Contains(door.gameObject.name)) return;

        registeredTransitDoors.Add(door.gameObject.name);

        graph.AddDoorEdges(
            corridorId,
            door.gameObject.name,
            door.elementType == DoorVarcoScript.ElementType.Door,
            door.transform.position,
            door.side,
            door.distFromA,
            door.distFromB,
            orderFW);

        Debug.Log($"[ExplMgr] Porta censita: {door.gameObject.name} corridoio={corridorId}");
    }
 
    // --------------------------------------------------------
    // HANDLER: VisionCone vede una porta dentro una stanza
    // --------------------------------------------------------
    private void HandleDoorVisibleInRoom(DoorVarcoScript door)
    {
        if (state != State.Rotating360 && state != State.InsideRoom) return;
        if (!inRoomMode) return;
        if (registeredRoomDoors.Contains(door.gameObject.name)) return;

        registeredRoomDoors.Add(door.gameObject.name);
        door.discovered = true;

        float navDist = door.NavMeshDistanceTo(transform.position);

        graph.AddRoomDoorEdge(
            currentNodeId,
            door.gameObject.name,
            door.elementType == DoorVarcoScript.ElementType.Door,
            door.transform.position,
            navDist);

        Debug.Log($"[ExplMgr] Porta in stanza: {door.gameObject.name} navDist={navDist:F1}");
    }
 
    // --------------------------------------------------------
    // HANDLER: connettore verticale raggiunto
    // --------------------------------------------------------
    private void HandleConnectorReached(string connectorId, int targetFloor, Vector3 pos)
    {
        if (state != State.MovingToConnector) return;
        if (pendingConnector == null || pendingConnector.id != connectorId) return;
        ExecuteFloorChange(targetFloor);
    }
 
    // ====================================================
    // STATI
    // ====================================================
 
    private void UpdateMovingToPole()
    {
        movingToPoleTimer += Time.deltaTime;

        // Fallback timeout: se dopo N secondi non siamo arrivati,
        // forziamo ProcessPoleTouched con la posizione del polo.
        // Copre il caso in cui il NavMesh non riesce ad avvicinarsi abbastanza.
        if (movingToPoleTimer >= poleTimeoutSeconds)
        {
            Debug.LogWarning($"[ExplMgr] Timeout polo {pendingCorridorId}/{pendingPoleId} " +
                             $"— forzo ProcessPoleTouched (dist={navAgent.remainingDistance:F2})");
            movingToPoleCorridorId = null;
            movingToPoleTimer = 0f;
            ProcessPoleTouched();
            return;
        }

        if (navAgent.pathPending) return;

        // Soglia più larga rispetto alle porte: i poli possono essere
        // vicino a muri e il NavMesh path termina prima della posizione esatta.
        if (navAgent.remainingDistance < poleArrivalThreshold)
        {
            movingToPoleCorridorId = null;
            movingToPoleTimer = 0f;
            ProcessPoleTouched();
        }
    }
 
    private void ProcessPoleTouched()
    {
        var corridorData = FindCorridorData(pendingCorridorId);
        if (corridorData == null) return;
 
        bool isFirstPole = (state != State.Transiting);
 
        if (isFirstPole)
        {
            poleAPos = pendingPolePos;

            Vector3 targetB = pendingPoleId == "1"
                ? corridorData.Pole2Position
                : corridorData.Pole1Position;
            poleBPos = targetB;
 
            float centralLen = Vector3.Distance(poleAPos, poleBPos);
            graph.AddCorridorPoles(pendingCorridorId, poleAPos, poleBPos, centralLen);
 
            // Salva il nodo precedente PRIMA di sovrascrivere currentNodeId.
            // Se venivamo da un polo B di un altro corridoio, crea l'arco segmento
            // che collega la fine del corridoio precedente all'inizio di questo.
            string comingFromNodeId = currentNodeId;

            string poleAId = $"{pendingCorridorId}_A";
            currentNodeId  = poleAId;
            graph.EnterNode(poleAId);

            // Arco segmento corridoio N_B → corridoio N+1_A (50cm default)
            // Solo se il corridoio di provenienza è DIVERSO da quello corrente
            if (!string.IsNullOrEmpty(comingFromNodeId) && comingFromNodeId.EndsWith("_B"))
            {
                string comingCorridor = comingFromNodeId.Replace("_B", "");
                if (comingCorridor != pendingCorridorId)
                    graph.AddSegmentArc(comingFromNodeId, poleAId, 0.5f);
            }
 
            transitCorridorId = pendingCorridorId;
            registeredTransitDoors.Clear(); // nuovo transito: riparte da zero
 
            navAgent.isStopped = false;
            navAgent.SetDestination(targetB);
            explorationVisionCone?.StartTransitScan(poleAPos, targetB, pendingCorridorId);
            inRoomMode = false;
            state      = State.Transiting;
 
            Debug.Log($"[ExplMgr] Transito {pendingCorridorId}: A={poleAPos} → B={targetB}");
        }
        else
        {
            explorationVisionCone?.StopTransitScan();
 
            string poleBId = $"{pendingCorridorId}_B";
            currentNodeId  = poleBId;
            graph.EnterNode(poleBId);
 
            graph.ComputeBackwardOrders(pendingCorridorId);
 
            string poleAId   = $"{pendingCorridorId}_A";
            string centralId = $"central_{pendingCorridorId}";
            graph.MarkEdgeExplored(poleAId, centralId, poleBId);
 
            movingToPoleCorridorId = null; // reset dopo transito completato
            Debug.Log($"[ExplMgr] Polo B toccato. Inizio ispezione {pendingCorridorId}.");
 
            StartRotation360(isRoom: false);
        }
 
        pendingCorridorId = null;
        pendingPoleId     = null;
    }
 
    private void UpdateTransiting()
    {
        if (navAgent.pathPending) return;
        if (navAgent.remainingDistance < arrivalThreshold * 0.5f)
        {
            pendingCorridorId = transitCorridorId;
            pendingPoleId     = "B_fallback";
            pendingPolePos    = poleBPos;
            ProcessPoleTouched();
        }
    }
 
    private void StartRotation360(bool isRoom)
    {
        inRoomMode    = isRoom || true; // sempre true: VisionCone scansiona varchi
        rotationAccum = 0f;
        navAgent.isStopped = true;
        state = State.Rotating360;
        Debug.Log($"[ExplMgr] 360° in '{currentNodeId}' (inRoomMode={inRoomMode})");
    }
 
    private void UpdateRotating360()
    {
        navAgent.isStopped = true;
        float step = rotationSpeed * Time.deltaTime;
        transform.Rotate(0, step, 0);
        rotationAccum += step;
 
        if (rotationAccum >= 360f)
        {
            navAgent.isStopped = false;
            rotationAccum      = 0f;
            Debug.Log($"[ExplMgr] 360° completato in '{currentNodeId}'.");
            DecideNextAction();
        }
    }
 
    private void UpdateMovingToDoor()
    {
        if (navAgent.pathPending) return;
        if (navAgent.remainingDistance <= inspectionDistance)
        {
            navAgent.isStopped = true;
            state              = State.InspectingDoor;
            Debug.Log($"[ExplMgr] Davanti a: {currentEdge?.id}");
        }
    }
 
    private void UpdateInspectingDoor()
    {
        if (currentEdge == null) { DecideNextAction(); return; }
 
        if (currentEdge.doorState == DoorState.Locked)
        {
            graph.MarkEdgeExplored(currentNodeId, currentEdge.id, null);
            currentEdge = null;
            navAgent.isStopped = false;
            DecideNextAction();
            return;
        }
 
        var door = FindDoorScript(currentEdge.id);
        if (door != null && !door.IsTraversable)
        {
            if (!door.TryOpen())
            {
                currentEdge.doorState = DoorState.Locked;
                graph.MarkEdgeExplored(currentNodeId, currentEdge.id, null);
                currentEdge = null;
                navAgent.isStopped = false;
                DecideNextAction();
                return;
            }
        }
 
        EnterThroughEdge(currentEdge);
    }
 
    private void UpdateEnteringRoom()
    {
        if (navAgent.pathPending) return;
        if (navAgent.remainingDistance < arrivalThreshold)
            StartRotation360(isRoom: true);
    }
 
    private void UpdateInsideRoom()
    {
        StartRotation360(isRoom: true);
    }
 
    // ─── Backtracking ─────────────────────────────────────
    // FIX: guard su pathPending aggiunta per evitare che l'agente
    // creda di essere "arrivato" appena dopo SetDestination (frame 0).
    private void UpdateBacktracking()
    {
        if (navAgent.pathPending) return;

        backtrackTimer += Time.deltaTime;

        bool arrived    = navAgent.hasPath && navAgent.remainingDistance <= arrivalThreshold;
        bool pathFailed = !navAgent.hasPath;
        bool timedOut   = backtrackTimer >= backtrackTimeout;

        if (arrived || pathFailed || timedOut)
        {
            backtrackTimer = 0f;
            if (timedOut && !arrived)
                Debug.LogWarning($"[ExplMgr] Backtrack timeout verso {graph.CurrentNodeId} " +
                                 $"(dist={navAgent.remainingDistance:F2}, hasPath={navAgent.hasPath}). Forzo avanzamento.");

            currentNodeId = graph.CurrentNodeId;
            if (string.IsNullOrEmpty(currentNodeId)) { CheckFloorCompletion(); return; }
            Debug.Log($"[ExplMgr] Backtrack completato in {currentNodeId}. Prossima azione!");
            state = State.Idle;
            DecideNextAction();
        }
    }

    private void UpdateMovingToConnector()
    {
        if (navAgent.pathPending) return;
        if (pendingConnector != null &&
            navAgent.remainingDistance <= arrivalThreshold * 2f)
            ExecuteFloorChange(pendingConnector.floorTo);
    }
 
    // ====================================================
    // DECISIONE PROSSIMA AZIONE
    // ====================================================
    private void DoBacktrack()
    {
        string    targetNode = null;
        GraphNode targetData = null;
 
        while (true)
        {
            targetNode = graph.Backtrack();
            if (targetNode == null) { CheckFloorCompletion(); return; }
 
            targetData = graph.GetNode(targetNode);
            if (targetData != null && targetData.HasUndiscoveredEdges()) break;
 
            Debug.Log($"[ExplMgr] {targetNode} già esplorato, riavvolgo...");
        }
 
        backtrackTimer = 0f;
        navAgent.isStopped = false;
        navAgent.SetDestination(targetData.position);
        state = State.Backtracking;
        Debug.Log($"[ExplMgr] Backtrack fisico → {targetNode}");
    }
 
    private void DecideNextAction()
    {
        if (string.IsNullOrEmpty(currentNodeId)) return;
 
        var node = graph.GetNode(currentNodeId);
        if (node == null) { DoBacktrack(); return; }
 
        Debug.Log($"[Debug] Decido azione per {currentNodeId}. Tipo={node.type}");
 
        GraphEdge nextEdge = null;
 
        if (node.type == NodeType.CorridorPoleB)
        {
            nextEdge = graph.NextCorridorEdgeToInspect(currentNodeId);
            if (nextEdge == null)
            {
                nextEdge = graph.NextRoomEdgeToInspect(currentNodeId);
                if (nextEdge != null)
                    Debug.Log("[ExplMgr] Nessuna porta BW, provo varchi visti col 360°.");
            }
        }
        else if (node.type == NodeType.Room)
        {
            nextEdge = graph.NextRoomEdgeToInspect(currentNodeId);
        }
        else
        {
            nextEdge = graph.NextEdgeToInspect(currentNodeId);
        }
 
        if (nextEdge != null)
        {
            currentEdge        = nextEdge;
            navAgent.isStopped = false;
            navAgent.SetDestination(nextEdge.position);
            state = State.MovingToDoor;
            Debug.Log($"[ExplMgr] Prossima porta: {nextEdge.id} navDist={nextEdge.navMeshDist:F1}");
        }
        else
        {
            DoBacktrack();
        }
    }

    // ====================================================
    // ENTRA ATTRAVERSO UN ARCO
    // ====================================================
    private void EnterThroughEdge(GraphEdge edge)
    {
        var doorScript = FindDoorScript(edge.id);
 
        string newNodeId = doorScript != null
            ? doorScript.GetRoomNameBeyondDoor(transform.position)
            : edge.id;
 
        bool isTargetCorridor = newNodeId.ToLower().Contains("corridoio");
 
        var existingNode = graph.GetNode(newNodeId);
        if (existingNode != null && !existingNode.HasUndiscoveredEdges() && !isTargetCorridor)
        {
            Debug.LogWarning($"[ExplMgr] LOOP EVITATO: '{newNodeId}' già completata.");
            graph.MarkEdgeExplored(currentNodeId, edge.id, newNodeId);
            SyncTwinCorridorEdge(edge.id, newNodeId);
            currentEdge = null;
            navAgent.isStopped = false;
            DecideNextAction();
            return;
        }
 
        graph.MarkEdgeExplored(currentNodeId, edge.id, isTargetCorridor ? null : newNodeId);
        SyncTwinCorridorEdge(edge.id, newNodeId);
 
       if (isTargetCorridor)
        {
            // --- RIPRISTINA LA FIX QUI ---
            if (graph.IsCorridorFullyTransited(newNodeId))
            {
                Debug.LogWarning($"[ExplMgr] Corridoio '{newNodeId}' già transitato → backtrack diretto.");
                currentEdge = null;
                navAgent.isStopped = false;
                DoBacktrack();
                return;
            }
            // -----------------------------

            Debug.Log($"[ExplMgr] Transito verso corridoio '{newNodeId}'. Attendo polo.");
            state = State.Idle;
        }
        else
        {
            graph.AddRoomNode(newNodeId, edge.position);
            currentNodeId = newNodeId;
            graph.EnterNode(currentNodeId);
            state = State.EnteringRoom;
        }
 
        Vector3 doorNormal = doorScript != null ? doorScript.transform.forward : transform.forward;
        Vector3 toDoor = (edge.position - transform.position).normalized;
        if (Vector3.Dot(doorNormal, toDoor) < 0) doorNormal = -doorNormal;
 
        Vector3 enterTarget = edge.position + doorNormal * enterOffset;
        navAgent.isStopped = false;
        navAgent.SetDestination(enterTarget);
        currentEdge = null;
 
        Debug.Log($"[ExplMgr] Spostamento fisico oltre la porta verso: {newNodeId}");
    }
 
    private void SyncTwinCorridorEdge(string edgeId, string toNodeId)
    {
        if (string.IsNullOrEmpty(currentNodeId)) return;
 
        if (currentNodeId.EndsWith("_B") && edgeId.StartsWith("bw_"))
        {
            string poleA  = currentNodeId.Replace("_B", "_A");
            string fwEdge = edgeId.Replace("bw_", "fw_");
            graph.MarkEdgeExplored(poleA, fwEdge, toNodeId);
        }
        else if (currentNodeId.EndsWith("_A") && edgeId.StartsWith("fw_"))
        {
            string poleB  = currentNodeId.Replace("_A", "_B");
            string bwEdge = edgeId.Replace("fw_", "bw_");
            graph.MarkEdgeExplored(poleB, bwEdge, toNodeId);
        }
    }

    // ====================================================
    // COMPLETAMENTO PIANO
    // ====================================================
    private void CheckFloorCompletion()
    {
        if (buildingGraph == null) { OnAllFloorsCompleted(); return; }
 
        var floorData = buildingGraph.GetFloor(currentFloor);
        if (!IsCurrentFloorFullyExplored(floorData))
        {
            NavigateToUnexploredCorridor(floorData);
            return;
        }
 
        if (floorData != null) floorData.explored = true;
        Debug.Log($"[ExplMgr] Piano {currentFloor} completato.");
 
        var nextFloor = buildingGraph.GetNextFloor(currentFloor);
        if (nextFloor == null) { OnAllFloorsCompleted(); return; }
 
        var connectors = buildingGraph.GetConnectors(currentFloor, nextFloor.floorIndex);
        if (connectors.Count == 0) { OnAllFloorsCompleted(); return; }
 
        connectors.Sort((a, b) => a.costUp.CompareTo(b.costUp));
        pendingConnector = connectors[0];
        navAgent.SetDestination(pendingConnector.triggerStartPosition);
        state = State.MovingToConnector;
    }
 
    private bool IsCurrentFloorFullyExplored(FloorNode floorData) => graph.IsFullyExplored();
 
    private void NavigateToUnexploredCorridor(FloorNode floorData)
    {
        string targetId = graph.FindNearestNodeWithDiscoveredEdges(transform.position);
        if (targetId == null) { OnAllFloorsCompleted(); return; }
 
        navAgent.SetDestination(graph.GetNode(targetId).position);
        currentNodeId = targetId;
        state         = State.Backtracking;
    }
 
    private void ExecuteFloorChange(int targetFloor)
    {
        currentFloor     = targetFloor;
        pendingConnector = null;
        registeredRoomDoors.Clear();
        registeredTransitDoors.Clear();
        graph = new TopologicalGraph();
 
        var floorData = buildingGraph?.GetFloor(targetFloor);
        if (floorData != null)
        {
            navAgent.Warp(floorData.spawnPosition);
            currentNodeId = $"floor_{targetFloor}_entry";
            graph.AddRoomNode(currentNodeId, floorData.spawnPosition);
            graph.EnterNode(currentNodeId);
        }
 
        inRoomMode = false;
        movingToPoleCorridorId = null;
        StartRotation360(isRoom: false);
        Debug.Log($"[ExplMgr] Piano {targetFloor} — nuova esplorazione.");
    }
 
    private void OnAllFloorsCompleted()
    {
        explorationVisionCone?.SetExplorationMode(false);
        GetComponent<BeliefTransmitter>()?.TransmitGraph(graph);
        state = State.Completed;
        Debug.Log("[ExplMgr] Edificio completamente esplorato.");
    }
 
    // ====================================================
    // HELPER
    // ====================================================
    private CorridorData FindCorridorData(string id)
    {
        foreach (var c in FindObjectsByType<CorridorData>(FindObjectsSortMode.None))
            if (c.corridorId == id) return c;
        return null;
    }
 
    private DoorVarcoScript FindDoorScript(string name)
    {
        string cleanName = name;
        if (name.StartsWith("fw_"))   cleanName = name.Substring(3);
        if (name.StartsWith("bw_"))   cleanName = name.Substring(3);
        if (name.StartsWith("room_")) cleanName = name.Substring(5);
        return GameObject.Find(cleanName)?.GetComponent<DoorVarcoScript>();
    }
}