using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
 
// ============================================================
// ExplorationManager.cs  — v2.2
// MODIFICHE rispetto a v2.1:
//   1. ProcessPoleTouched (polo B): chiama ComputeForwardOrders +
//      ComputeBackwardOrders + ComputeExplorationOrder in sequenza.
//   2. UpdateRotating360 (dopo 360° al polo B): ricalcola BW e
//      ExplorationOrder per includere le porte scoperte visivamente.
//   3. HandleDoorVisibleInRoom (polo B): dopo AddDoorEdges ricalcola
//      subito BW + ExplorationOrder così il viewer è sempre aggiornato.
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
    public float poleArrivalThreshold  = 1.2f;
    public float rotationSpeed         = 120f;
    public float inspectionDistance    = 1.2f;
    public float enterOffset           = 1.5f;
    public float poleTimeoutSeconds    = 4f;
 
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
        MovingThroughStairs,
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
    private HashSet<string> allCorridorDoors       = new HashSet<string>();
 
    private bool explorationStarted = false;
    private int  currentFloor;
    private VerticalConnector pendingConnector;
    private HashSet<string> traversedConnectors = new HashSet<string>();
    private Vector3 stairsReturnPosition = Vector3.zero;
private bool    needsStairsReturn    = false;
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
            case State.MovingThroughStairs: UpdateMovingThroughStairs(); break; 
        }
    }
 
    // --------------------------------------------------------
    // HANDLER: VisionCone vede un polo di corridoio
    // --------------------------------------------------------
    private void HandleCorridorPoleVisible(string corridorId, string poleId, Vector3 polePos)
    {
        if (state == State.MovingToPole || state == State.Transiting) return;
        if (state == State.Rotating360) return;
        if (state != State.Idle && state != State.Backtracking) return;

        var corridorData = FindCorridorData(corridorId);
        if (corridorData == null) return;

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

        if (movingToPoleCorridorId == corridorId) return;

        pendingCorridorId      = corridorId;
        pendingPoleId          = nearestPoleId;
        pendingPolePos         = nearestPolePos;
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

        if (state == State.Transiting && corridorId == transitCorridorId)
        {
            pendingCorridorId = corridorId;
            pendingPoleId     = poleId;
            pendingPolePos    = polePos;
            ProcessPoleTouched();
            return;
        }

        if (state == State.MovingToPole && corridorId == pendingCorridorId)
        {
            pendingPolePos = polePos;
            ProcessPoleTouched();
            return;
        }

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

    // ── NUOVO: ignora i varchi che portano a un altro corridoio ──────────
    // Questi non sono stanze: vengono già gestiti dal transito polo A→B.
    // Aggiungere archi fw/bw per loro inquina l'ordinamento e crea
    // exploration order sbagliati (il log mostra Varco_Prefab_corr1_2
    // come arco bw in corridoio1 ma non è una stanza).
    string roomBeyond = door.GetRoomNameBeyondDoor(transform.position);
    bool isCorrLink = !string.IsNullOrEmpty(roomBeyond) 
                  && roomBeyond.ToLower().Contains("corridoio");


   /* if (!string.IsNullOrEmpty(roomBeyond) && roomBeyond.ToLower().Contains("corridoio"))
    {
        Debug.Log($"[ExplMgr] Varco {door.gameObject.name} → corridoio '{roomBeyond}' " +
                  $"— ignorato come porta, è un passaggio tra corridoi.");
        return;
    }
    // ────────────────────────────────────────────────────────────────────
*/
    registeredTransitDoors.Add(door.gameObject.name);
    allCorridorDoors.Add(door.gameObject.name);

    string computedSide = ComputeSideRelativeToCorridorAxis(
        door.transform.position, poleAPos, poleBPos);

    graph.AddDoorEdges(
        corridorId,
        door.gameObject.name,
        door.elementType == DoorVarcoScript.ElementType.Door,
        door.transform.position,
        computedSide,
        door.distFromA,
        door.distFromB,
        0,
        isCorridorLink: isCorrLink);   // ← passa il flag

    Debug.Log($"[ExplMgr] Porta censita: {door.gameObject.name} " +
            $"corridoio={corridorId} side={computedSide} " +
            $"corridorLink={isCorrLink}");
    }
    // ── Calcola il lato di una porta rispetto all'asse fisso corridoio A→B ──
    // Restituisce "RIGHT" o "LEFT".
    // Porte frontali (angolo≈0 rispetto all'asse) → "RIGHT" per convenzione.
    private string ComputeSideRelativeToCorridorAxis(Vector3 doorPos, Vector3 posA, Vector3 posB)
    {
        Vector3 axis   = (posB - posA).normalized;
        axis.y = 0f;
        Vector3 toDoor = doorPos - posA;
        toDoor.y = 0f;
        toDoor   = toDoor.normalized;

        // Cross product 2D (componente Y): > 0 = LEFT (sinistra guardando da A verso B)
        float cross = axis.x * toDoor.z - axis.z * toDoor.x;
        if (Mathf.Abs(cross) < 0.05f) return "RIGHT"; // frontale → RIGHT per convenzione
        return cross > 0f ? "LEFT" : "RIGHT";
    }

    // --------------------------------------------------------
    // HANDLER: VisionCone vede una porta dentro una stanza
    // --------------------------------------------------------
    private void HandleDoorVisibleInRoom(DoorVarcoScript door)
    {
        if (state != State.Rotating360 && state != State.InsideRoom) return;
    if (!inRoomMode) return;
    if (registeredRoomDoors.Contains(door.gameObject.name)) return;

    // NON aggiungere porte che sono ingressi/uscite di scale o ascensori
    // Altrimenti il 360° nel box aggiunge Door_Box come room door → loop
    if (IsConnectorDoor(door.gameObject.name))
    {
        Debug.Log($"[ExplMgr] {door.gameObject.name} è porta connettore → ignorata nel 360°");
        return;
    }
        registeredRoomDoors.Add(door.gameObject.name);
        door.discovered = true;

        var node = graph.GetNode(currentNodeId);

        if (node != null && node.type == NodeType.CorridorPoleB)
        {
            string corridorId = currentNodeId.Replace("_B", "");

            if (allCorridorDoors.Contains(door.gameObject.name))
            {
                Debug.Log($"[ExplMgr] {door.gameObject.name} già censita → ignorata");
                return;
            }

            float dB = Vector3.Distance(door.transform.position, poleBPos);
            float dA = Vector3.Distance(door.transform.position, poleAPos);

            if (dB > 0.6f)
            {
                Debug.Log($"[ExplMgr] {door.gameObject.name} fuori corridoio {corridorId} " +
                        $"(dB={dB:F2} > 0.6) → ignorata");
                return;
            }

            registeredTransitDoors.Add(door.gameObject.name);
            allCorridorDoors.Add(door.gameObject.name);

            // Calcola side sull'asse fisso A→B anche qui
            string computedSide = ComputeSideRelativeToCorridorAxis(
                door.transform.position, poleAPos, poleBPos);

            graph.AddDoorEdges(corridorId, door.gameObject.name,
                door.elementType == DoorVarcoScript.ElementType.Door,
                door.transform.position, computedSide, dA, dB, orderFW: 0);

            // ── MODIFICA v2.2: ricalcola tutti e tre gli ordini dopo ogni nuova porta ──
            graph.ComputeForwardOrders(corridorId);
            graph.ComputeBackwardOrders(corridorId);
            graph.ComputeExplorationOrder(corridorId);

            Debug.Log($"[ExplMgr] Porta polo-B → {corridorId}: {door.gameObject.name} " +
                      $"dA={dA:F1} dB={dB:F1} side={computedSide}");
            return;
        }

        // Nodo stanza normale → room edge
        float navDist = door.NavMeshDistanceTo(transform.position);
        graph.AddRoomDoorEdge(currentNodeId, door.gameObject.name,
            door.elementType == DoorVarcoScript.ElementType.Door,
            door.transform.position, navDist);
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
 
            string comingFromNodeId = currentNodeId;

            string poleAId = $"{pendingCorridorId}_A";
            currentNodeId  = poleAId;
            graph.EnterNode(poleAId);

            if (!string.IsNullOrEmpty(comingFromNodeId) && comingFromNodeId.EndsWith("_B"))
            {
                string comingCorridor = comingFromNodeId.Replace("_B", "");
                if (comingCorridor != pendingCorridorId)
                    graph.AddSegmentArc(comingFromNodeId, poleAId, 0.5f);
            }
 
            transitCorridorId = pendingCorridorId;
            registeredTransitDoors.Clear();
 
            navAgent.isStopped = false;
            navAgent.SetDestination(targetB);
            explorationVisionCone?.StartTransitScan(poleAPos, targetB, pendingCorridorId);
            inRoomMode = false;
            state      = State.Transiting;
 
            Debug.Log($"[ExplMgr] Transito {pendingCorridorId}: A={poleAPos} → B={targetB}");
        }
        else
        {
            // ── POLO B TOCCATO ───────────────────────────────────────────────────────
            explorationVisionCone?.StopTransitScan();
 
            string poleBId = $"{pendingCorridorId}_B";
            currentNodeId  = poleBId;
            graph.EnterNode(poleBId);
 
            // ── MODIFICA v2.2: calcola tutti e tre gli ordini in sequenza ────────────
            // 1. orderFW per lato (scala R e L separate, nessun buco nei numeri)
            graph.ComputeForwardOrders(pendingCorridorId);
            // 2. orderBW per lato (idem)
            graph.ComputeBackwardOrders(pendingCorridorId);
            // 3. explorationOrder: sequenza visita effettiva (clustering + zigzag R/L)
            graph.ComputeExplorationOrder(pendingCorridorId);
            // ────────────────────────────────────────────────────────────────────────

            string poleAId   = $"{pendingCorridorId}_A";
            string centralId = $"central_{pendingCorridorId}";
            graph.MarkEdgeExplored(poleAId, centralId, poleBId);
 
            movingToPoleCorridorId = null;
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
        inRoomMode    = isRoom;
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
            rotationAccum = 0f;
            Debug.Log($"[ExplMgr] 360° completato in '{currentNodeId}'.");

            // ── MODIFICA v2.2: dopo il 360° al polo B ricalcola tutti e tre gli ordini ──
            // Serve perché durante il 360° potrebbero essere state aggiunte porte
            // scoperte visivamente (HandleDoorVisibleInRoom) non viste durante il transito.
            if (currentNodeId != null && currentNodeId.EndsWith("_B"))
            {
                string corrId = currentNodeId.Replace("_B", "");
                graph.ComputeForwardOrders(corrId);     // aggiorna anche i FW gemelli
                graph.ComputeBackwardOrders(corrId);    // ricalcola per lato con nuove porte
                graph.ComputeExplorationOrder(corrId);  // ricalcola sequenza visita
                Debug.Log($"[ExplMgr] Ordini ricalcolati dopo 360° per {corrId}");
            }

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
 
    private float enteringRoomTimer   = 0f;
    private float enteringRoomTimeout = 3f;

    private void UpdateEnteringRoom()
    {
        if (navAgent.pathPending) return;
        enteringRoomTimer += Time.deltaTime;

        if (navAgent.remainingDistance < arrivalThreshold ||
            (!navAgent.hasPath && !navAgent.pathPending) ||
            enteringRoomTimer >= enteringRoomTimeout)
        {
            enteringRoomTimer = 0f;

            var stair = FindNearbyVerticalConnector(detectionRadius: 5f);
            if (stair != null)
            {
                var connector = buildingGraph?.connectors
                    .Find(c => c.id == stair.connectorId);
                if (connector != null)
                {
                    traversedConnectors.Add(stair.connectorId);
                    Debug.Log($"[ExplMgr] Scale '{stair.connectorId}' → navigo lungo la rampa");
                    pendingConnector   = connector;
                    navAgent.isStopped = false;
                    // Naviga fisicamente lungo la rampa fino a Door_Box
                    navAgent.SetDestination(connector.triggerEndPosition);
                    state = State.MovingThroughStairs;
                    return;
                }
            }
            StartRotation360(isRoom: true);
        }
    }

// ── Helper: cerca VerticalConnectorScript tramite layer "connector" ───────────
// Usa QueryTriggerInteraction.Collide perché il collider è un trigger.
    private VerticalConnectorScript FindNearbyVerticalConnector(float detectionRadius = 5f)
    {
        var hits = Physics.OverlapSphere(
            transform.position, detectionRadius,
            Physics.AllLayers, QueryTriggerInteraction.Collide);

        foreach (var col in hits)
        {
            var vcs = col.GetComponent<VerticalConnectorScript>();
            // Salta connettori già attraversati → impedisce il loop
            if (vcs != null && !traversedConnectors.Contains(vcs.connectorId))
                return vcs;
        }
        return null;
    }
    private void UpdateInsideRoom()
    {
        StartRotation360(isRoom: true);
    }
 
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
    // ── Agente sta percorrendo fisicamente le scale verso il piano inferiore ──────
    // Quando arriva al fondo, chiama ExecuteFloorChange.
    // Se la NavMesh non riesce a raggiungere triggerEndPosition entro il timeout
    // usiamo un timer di sicurezza.
    private float stairsTimer   = 0f;
    private const float StairsTimeout = 10f;

    private void UpdateMovingThroughStairs()
    {
        if (navAgent.pathPending) return;
        stairsTimer += Time.deltaTime;

        bool arrived  = navAgent.hasPath &&
                        navAgent.remainingDistance <= navAgent.stoppingDistance + arrivalThreshold;
        bool timedOut = stairsTimer >= StairsTimeout;
        if (!arrived && !timedOut) return;

        stairsTimer = 0f;

        var floorData = buildingGraph?.GetFloor(pendingConnector.floorTo);
        if (floorData?.spawnObject == null)
        {
            Debug.LogError("[ExplMgr] spawnObject del piano inferiore non assegnato!");
            return;
        }

        // Salva posizione attuale come punto di ritorno (cima scala, piano superiore)
        stairsReturnPosition = transform.position;
        needsStairsReturn    = true;

        navAgent.Warp(floorData.spawnPosition);

        string nodeId = floorData.spawnObject.name;
        if (graph.GetNode(nodeId) == null)
            graph.AddRoomNode(nodeId, floorData.spawnPosition);

        currentNodeId          = nodeId;
        graph.EnterNode(nodeId);
        pendingConnector       = null;
        movingToPoleCorridorId = null;
        inRoomMode             = true;

        Debug.Log($"[ExplMgr] Warp a '{nodeId}'. Ritorno salvato: {stairsReturnPosition}");
        StartRotation360(isRoom: true);
    }
 
    // ====================================================
    // DECISIONE PROSSIMA AZIONE
    // ====================================================
   private void DoBacktrack()
    {
        string targetNode = null;
        GraphNode targetData = null;

        while (true)
        {
            targetNode = graph.Backtrack();
            if (targetNode == null) { CheckFloorCompletion(); return; }

            targetData = graph.GetNode(targetNode);
            if (targetData != null && targetData.HasUndiscoveredEdges()) break;
            Debug.Log($"[Backtrack] {targetNode} già esplorato, riavvolgo...");
        }

        backtrackTimer = 0f;
        navAgent.isStopped = false;

        // Se dobbiamo tornare al piano superiore e il nodo target è lassù,
        // Warp al punto di ritorno (cima scala) prima di navigare
        if (needsStairsReturn)
        {
            float distToTarget = Vector3.Distance(transform.position, targetData.position);
            float distReturnToTarget = Vector3.Distance(stairsReturnPosition, targetData.position);

            // Se il target è più vicino alla cima scala che al piano attuale → siamo sul piano sbagliato
            if (distReturnToTarget < distToTarget - 1f)
            {
                Debug.Log($"[ExplMgr] Warp di ritorno a piano superiore: {stairsReturnPosition}");
                navAgent.Warp(stairsReturnPosition);
                needsStairsReturn = false;
            }
        }

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
            if (graph.IsCorridorFullyTransited(newNodeId))
            {
                Debug.LogWarning($"[ExplMgr] Corridoio '{newNodeId}' già transitato.");
                currentEdge = null;
                navAgent.isStopped = false;
                DecideNextAction();
                return;
            }
            Debug.Log($"[ExplMgr] Transito verso corridoio '{newNodeId}'.");
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

        // ── verifica che enterTarget sia sul NavMesh ─────────────────────────
        // Se non lo è, cerca il punto valido più vicino nel raggio di 2m.
        // Senza questo, il path fallisce istantaneamente e UpdateEnteringRoom
        // fa il 360° davanti alla porta invece che dentro la stanza.
        NavMeshHit hit;
        if (!NavMesh.SamplePosition(enterTarget, out hit, 2f, NavMesh.AllAreas))
        {
            // Fallback: prova dall'altra parte della porta
            enterTarget = edge.position - doorNormal * enterOffset;
            if (!NavMesh.SamplePosition(enterTarget, out hit, 2f, NavMesh.AllAreas))
            {
                // Ultimo fallback: usa direttamente la posizione della porta
                hit.position = edge.position;
                Debug.LogWarning($"[ExplMgr] NavMesh non trovato vicino a '{newNodeId}' " +
                                $"— uso posizione porta.");
            }
        }
        enterTarget = hit.position;
        
        navAgent.isStopped = false;
        navAgent.SetDestination(enterTarget);
        currentEdge = null;

        Debug.Log($"[ExplMgr] Entro in '{newNodeId}' → target NavMesh: {enterTarget}");
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
    
    // Rimuove connettori già percorsi inline durante l'esplorazione
    // senza questo, l'agente torna al Box anche se lo ha già visitato
    connectors.RemoveAll(c => traversedConnectors.Contains(c.id));
    
    if (connectors.Count == 0) 
    { 
        Debug.Log("[ExplMgr] Tutti i connettori già percorsi → esplorazione completa.");
        OnAllFloorsCompleted(); 
        return; 
    }

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

        var go = GameObject.Find(cleanName);
        if (go == null) return null;
        return go.GetComponentInChildren<DoorVarcoScript>();
    }
    private bool IsConnectorDoor(string doorName)
    {
        if (buildingGraph == null) return false;
        foreach (var connector in buildingGraph.connectors)
        {
            if (connector.triggerStartObject?.name == doorName) return true;
            if (connector.triggerEndObject?.name == doorName) return true;
        }
        return false;
    }
}