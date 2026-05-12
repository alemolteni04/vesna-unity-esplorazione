using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// ExplorationManager.cs  — v2.4
// MODIFICHE rispetto a v2.3:
//   ARCHITETTURA MULTI-GRAFO (un TopologicalGraph per piano)
//
//   • floorGraphs[N] contiene solo i nodi del piano N.
//   • La proprietà `graph` restituisce sempre floorGraphs[currentFloor],
//     quindi tutto il codice di esplorazione resta invariato.
//   • Quando l'agente attraversa un connettore verticale, ExecuteFloorChange:
//       1. Aggiunge "conn_{connectorId}" al grafo piano CORRENTE
//          (pos = triggerStartPosition, es. Door_Cor1-Box su piano 1).
//       2. Crea/recupera il grafo piano DESTINAZIONE.
//       3. Aggiunge "conn_{connectorId}" al grafo piano DESTINAZIONE
//          (pos = triggerEndPosition, es. Door_Box su piano 0).
//       4. Registra il link in ConnectorLinks (struttura pubblica).
//   • BeliefTransmitter può accedere ad AllFloorGraphs e ConnectorLinks
//     per ricostruire il grafo completo dell'edificio.
// ============================================================

// ─────────────────────────────────────────────────────────────
// STRUTTURA DATI: link cross-floor tra due grafi di piano
// ─────────────────────────────────────────────────────────────
[System.Serializable]
public struct FloorConnectorLink
{
    public string  connectorId;
    public int     floorFrom;
    public string  nodeIdFrom;    // ID nodo in floorGraphs[floorFrom]
    public Vector3 posFrom;       // posizione fisica lato partenza
    public int     floorTo;
    public string  nodeIdTo;      // ID nodo in floorGraphs[floorTo]
    public Vector3 posTo;         // posizione fisica lato arrivo
}

// ─────────────────────────────────────────────────────────────

public class ExplorationManager : MonoBehaviour
{
    [Header("Agente")]
    public NavMeshAgent          navAgent;
    public ExplorationVisionCone explorationVisionCone;

    [Header("Macro-Grafo edificio")]
    public BuildingGraph buildingGraph;

    [Header("Piano iniziale")]
    public int startingFloor = 0;

    [Header("Stanza di partenza")]
    [Tooltip("Trascina il GameObject stanza di partenza. Il nome è l'ID nodo iniziale.")]
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
        Idle, MovingToPole, Transiting, WaitingAtPoleB,
        Rotating360, MovingToDoor, InspectingDoor,
        EnteringRoom, InsideRoom, Backtracking,
        MovingToConnector, Completed
    }

    private State state = State.Idle;

    // ─────────────────────────────────────────────────────────
    // MULTI-GRAFO: un TopologicalGraph per piano
    // ─────────────────────────────────────────────────────────
    private Dictionary<int, TopologicalGraph> floorGraphs
        = new Dictionary<int, TopologicalGraph>();

    /// <summary>Grafo del piano corrente (proprietà calcolata).</summary>
    private TopologicalGraph graph
        => floorGraphs.TryGetValue(currentFloor, out var g) ? g : null;

    /// <summary>Tutti i grafi, accessibili da BeliefTransmitter.</summary>
    public Dictionary<int, TopologicalGraph> AllFloorGraphs => floorGraphs;

    /// <summary>
    /// Link cross-floor: descrive come il nodo conn_X nel grafo pianoA si
    /// collega al nodo conn_X nel grafo pianoB. Usato da BeliefTransmitter
    /// per ricostruire il grafo completo dell'edificio.
    /// </summary>
    public List<FloorConnectorLink> ConnectorLinks { get; }
        = new List<FloorConnectorLink>();

    // Alias pubblico per retrocompatibilità
    public TopologicalGraph Graph => graph;

    private string currentNodeId;
    public  string CurrentNodeId => currentNodeId;

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
    private HashSet<string>   traversedConnectors = new HashSet<string>();

    private string movingToPoleCorridorId;
    private float  movingToPoleTimer = 0f;
    private float  backtrackTimer    = 0f;
    public  float  backtrackTimeout  = 5f;

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

    // ──────────────────────────────────────────────────────────────
// METODO 1: StartExploration  (sostituisce l'intero metodo)
// ──────────────────────────────────────────────────────────────
public void StartExploration(string startNodeId, Vector3 startPos)
{
    if (explorationStarted) return;
    explorationStarted = true;
 
    // ── FIX BUG 1: auto-rileva il piano di partenza ──────────────────────
    // Cerca nel BuildingGraph quale FloorNode contiene startRoomObject
    // (come spawnObject o come uno dei roomObjects).
    // Se non trovato, usa startingFloor come fallback.
    currentFloor = startingFloor;  // fallback
    if (buildingGraph != null && startRoomObject != null)
    {
        foreach (var floor in buildingGraph.floors)
        {
            bool isSpawn = floor.spawnObject == startRoomObject;
            bool isRoom  = floor.roomObjects.Contains(startRoomObject);
            if (isSpawn || isRoom)
            {
                currentFloor = floor.floorIndex;
                Debug.Log($"[ExplMgr] Piano di partenza auto-rilevato: {currentFloor} " +
                          $"({floor.floorName})");
                break;
            }
        }
    }
 
    // Crea il grafo per il piano iniziale
    floorGraphs[currentFloor] = new TopologicalGraph();
 
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

        Vector3 myPos   = transform.position;
        float distPole1 = Vector3.Distance(myPos, corridorData.Pole1Position);
        float distPole2 = Vector3.Distance(myPos, corridorData.Pole2Position);

        Vector3 nearestPolePos;
        string  nearestPoleId;
        if (distPole1 <= distPole2)
        { nearestPolePos = corridorData.Pole1Position; nearestPoleId = "1"; }
        else
        { nearestPolePos = corridorData.Pole2Position; nearestPoleId = "2"; }

        if (movingToPoleCorridorId == corridorId) return;

        pendingCorridorId      = corridorId;
        pendingPoleId          = nearestPoleId;
        pendingPolePos         = nearestPolePos;
        movingToPoleCorridorId = corridorId;

        navAgent.isStopped = false;
        navAgent.SetDestination(nearestPolePos);
        movingToPoleTimer = 0f;
        state = State.MovingToPole;

        Debug.Log($"[ExplMgr] Polo {corridorId}/{nearestPoleId} individuato → mi avvicino.");
    }

    // --------------------------------------------------------
    // HANDLER: Polo fisicamente toccato
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
                    Debug.Log($"[ExplMgr] Polo {corridorId}_{poleId} ignorato — corridoio già transitato.");
                    return;
                }
            }
            pendingCorridorId = corridorId;
            pendingPoleId     = poleId;
            pendingPolePos    = polePos;
            ProcessPoleTouched();
            return;
        }

        Debug.Log($"[ExplMgr] Polo {corridorId}_{poleId} ignorato — stato={state}.");
    }

    // --------------------------------------------------------
    // HANDLER: DoorSensor scopre una porta durante il transito
    // --------------------------------------------------------
    private void HandleDoorDiscovered(DoorVarcoScript door, string corridorId, int orderFW)
    {
        if (state != State.Transiting) return;
        if (corridorId != transitCorridorId) return;
        if (registeredTransitDoors.Contains(door.gameObject.name)) return;

        string roomBeyond = door.GetRoomNameBeyondDoor(transform.position);
        bool isCorrLink   = !string.IsNullOrEmpty(roomBeyond)
                         && roomBeyond.ToLower().Contains("corridoio");

        registeredTransitDoors.Add(door.gameObject.name);
        allCorridorDoors.Add(door.gameObject.name);

        string computedSide = ComputeSideRelativeToCorridorAxis(
            door.transform.position, poleAPos, poleBPos);

        graph.AddDoorEdges(corridorId, door.gameObject.name,
            door.elementType == DoorVarcoScript.ElementType.Door,
            door.transform.position, computedSide,
            door.distFromA, door.distFromB, 0,
            isCorridorLink: isCorrLink);

        Debug.Log($"[ExplMgr] Porta censita: {door.gameObject.name} " +
                  $"corridoio={corridorId} side={computedSide} corridorLink={isCorrLink}");
    }

    private string ComputeSideRelativeToCorridorAxis(Vector3 doorPos, Vector3 posA, Vector3 posB)
    {
        Vector3 axis   = (posB - posA).normalized; axis.y = 0f;
        Vector3 toDoor = (doorPos - posA).normalized; toDoor.y = 0f;
        float cross = axis.x * toDoor.z - axis.z * toDoor.x;
        if (Mathf.Abs(cross) < 0.05f) return "RIGHT";
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

        // Le porte connettore (scale/ascensori) vengono ignorate nel 360°.
        // L'agente non aggiungerà archi verso di esse durante l'esplorazione
        // del piano corrente — verranno usate solo da CheckFloorCompletion.
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
            { Debug.Log($"[ExplMgr] {door.gameObject.name} già censita → ignorata"); return; }

            float dB = Vector3.Distance(door.transform.position, poleBPos);
            float dA = Vector3.Distance(door.transform.position, poleAPos);

            if (dB > 0.6f)
            { Debug.Log($"[ExplMgr] {door.gameObject.name} fuori corridoio (dB={dB:F2}) → ignorata"); return; }

            registeredTransitDoors.Add(door.gameObject.name);
            allCorridorDoors.Add(door.gameObject.name);

            string computedSide = ComputeSideRelativeToCorridorAxis(
                door.transform.position, poleAPos, poleBPos);

            graph.AddDoorEdges(corridorId, door.gameObject.name,
                door.elementType == DoorVarcoScript.ElementType.Door,
                door.transform.position, computedSide, dA, dB, orderFW: 0);

            graph.ComputeForwardOrders(corridorId);
            graph.ComputeBackwardOrders(corridorId);
            graph.ComputeExplorationOrder(corridorId);

            Debug.Log($"[ExplMgr] Porta polo-B → {corridorId}: {door.gameObject.name} " +
                      $"dA={dA:F1} dB={dB:F1} side={computedSide}");
            return;
        }

        float navDist = door.NavMeshDistanceTo(transform.position);
        graph.AddRoomDoorEdge(currentNodeId, door.gameObject.name,
            door.elementType == DoorVarcoScript.ElementType.Door,
            door.transform.position, navDist);
        Debug.Log($"[ExplMgr] Porta in stanza: {door.gameObject.name} navDist={navDist:F1}");
    }

    // --------------------------------------------------------
    // HANDLER: connettore verticale raggiunto (trigger fisico)
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
            Debug.LogWarning($"[ExplMgr] Timeout polo {pendingCorridorId}/{pendingPoleId}.");
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

            graph.AddCorridorPoles(pendingCorridorId, poleAPos, poleBPos,
                Vector3.Distance(poleAPos, poleBPos));

            string comingFromNodeId = currentNodeId;
            string poleAId          = $"{pendingCorridorId}_A";
            currentNodeId           = poleAId;
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
            explorationVisionCone?.StopTransitScan();

            string poleBId = $"{pendingCorridorId}_B";
            currentNodeId  = poleBId;
            graph.EnterNode(poleBId);

            graph.ComputeForwardOrders(pendingCorridorId);
            graph.ComputeBackwardOrders(pendingCorridorId);
            graph.ComputeExplorationOrder(pendingCorridorId);

            graph.MarkEdgeExplored($"{pendingCorridorId}_A",
                $"central_{pendingCorridorId}", poleBId);

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

            if (currentNodeId != null && currentNodeId.EndsWith("_B"))
            {
                string corrId = currentNodeId.Replace("_B", "");
                graph.ComputeForwardOrders(corrId);
                graph.ComputeBackwardOrders(corrId);
                graph.ComputeExplorationOrder(corrId);
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
            StartRotation360(isRoom: true);
        }
    }

    private void UpdateInsideRoom() => StartRotation360(isRoom: true);

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
                Debug.LogWarning($"[ExplMgr] Backtrack timeout → {graph.CurrentNodeId}.");

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
            Debug.Log($"[Backtrack] {targetNode} già esplorato, riavvolgo...");
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
            nextEdge = graph.NextRoomEdgeToInspect(currentNodeId);
        else
            nextEdge = graph.NextEdgeToInspect(currentNodeId);

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

        // Se la porta è un connettore verticale (scala/ascensore):
        // - usa l'ID del connettore come nome nodo (es. "scalaBox")
        // - NON entrare fisicamente: marca l'arco e prosegui
        if (doorScript != null && IsConnectorDoor(doorScript.gameObject.name))
        {
            var connector = FindConnectorForDoor(doorScript.gameObject.name);
            if (connector != null) newNodeId = connector.id;  // "scalaBox"

            if (graph.GetNode(newNodeId) == null)
                graph.AddRoomNode(newNodeId, edge.position);

            graph.MarkEdgeExplored(currentNodeId, edge.id, newNodeId);
            SyncTwinCorridorEdge(edge.id, newNodeId);

            currentEdge = null;
            navAgent.isStopped = false;
            DecideNextAction();
            return;
        }

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
        Vector3 toDoor     = (edge.position - transform.position).normalized;
        if (Vector3.Dot(doorNormal, toDoor) < 0) doorNormal = -doorNormal;
        Vector3 enterTarget = edge.position + doorNormal * enterOffset;

        NavMeshHit hit;
        if (!NavMesh.SamplePosition(enterTarget, out hit, 2f, NavMesh.AllAreas))
        {
            enterTarget = edge.position - doorNormal * enterOffset;
            if (!NavMesh.SamplePosition(enterTarget, out hit, 2f, NavMesh.AllAreas))
            {
                hit.position = edge.position;
                Debug.LogWarning($"[ExplMgr] NavMesh non trovato vicino a '{newNodeId}'.");
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

        // Cerca il prossimo piano da esplorare, in ordine di floorIndex crescente
        var unexplored = buildingGraph.GetUnexploredFloors();
        unexplored.Sort((a, b) => a.floorIndex.CompareTo(b.floorIndex));

        foreach (var targetFloor in unexplored)
        {
            var connectors = buildingGraph.GetConnectors(currentFloor, targetFloor.floorIndex);
            connectors.RemoveAll(c => traversedConnectors.Contains(c.id));
            if (connectors.Count == 0) continue;

            connectors.Sort((a, b) => a.costUp.CompareTo(b.costUp));
            pendingConnector = connectors[0];

            Debug.Log($"[ExplMgr] Piano {currentFloor} → piano {targetFloor.floorIndex} " +
                      $"via '{pendingConnector.id}'");

            navAgent.isStopped = false;
            navAgent.SetDestination(pendingConnector.triggerStartPosition);
            state = State.MovingToConnector;
            return;
        }

        Debug.Log("[ExplMgr] Tutti i piani esplorati.");
        OnAllFloorsCompleted();
    }

 // ──────────────────────────────────────────────────────────────
// METODO 2: IsCurrentFloorFullyExplored  (sostituisce l'intero metodo)
// ──────────────────────────────────────────────────────────────
private bool IsCurrentFloorFullyExplored(FloorNode floorData)
{
    // 1. Il grafo del piano corrente deve avere tutti gli archi esplorati
    if (!graph.IsFullyExplored()) return false;
 
    // 2. Tutti i roomObjects del BuildingGraph per questo piano devono
    //    essere stati visitati come nodi nel grafo.
    if (floorData != null && floorData.roomObjects.Count > 0)
    {
        foreach (string roomId in floorData.roomIds)
        {
            // Check diretto: nodo con ID uguale al nome del GameObject
            if (graph.GetNode(roomId) != null) continue;
 
            // ── FIX BUG 2: i corridoi nel grafo si chiamano {id}_A e {id}_B ──
            // Un corridoio nel BuildingGraph si chiama es. "corridoio1",
            // ma nel TopologicalGraph esiste come "corridoio1_A" e "corridoio1_B".
            // Se almeno il polo A è presente, il corridoio è stato attraversato.
            if (graph.GetCorridorPoleNode($"{roomId}_A") != null) continue;
 
            // Stanza/corridoio non ancora visitato
            Debug.Log($"[ExplMgr] Piano {currentFloor}: '{roomId}' non ancora visitata.");
            return false;
        }
    }
 
    return true;
}

    private void NavigateToUnexploredCorridor(FloorNode floorData)
    {
        string targetId = graph.FindNearestNodeWithDiscoveredEdges(transform.position);
        if (targetId == null) { OnAllFloorsCompleted(); return; }

        navAgent.SetDestination(graph.GetNode(targetId).position);
        currentNodeId = targetId;
        state         = State.Backtracking;
    }

    // ====================================================
    // CAMBIO PIANO  —  v2.4: ARCHITETTURA MULTI-GRAFO
    // ====================================================
    //
    //  PRIMA della chiamata (es. piano 1 → piano 0):
    //    floorGraphs[1]: soggiorno, corridoio1, corridoio2, camera1, camera2,
    //                    bagno1, bagno2, cucina  (grafo del piano 1)
    //
    //  DOPO la chiamata:
    //    floorGraphs[1]: + nodo "conn_scalaBox" (pos=Door_Cor1-Box, piano 1)
    //    floorGraphs[0]: nodo "conn_scalaBox" (pos=Door_Box, piano 0)
    //                  + nodo "box"           (spawn piano 0)   ← currentNode
    //
    //    ConnectorLinks: { scalaBox, piano1/"conn_scalaBox" ↔ piano0/"conn_scalaBox" }
    //
    //  I due grafi restano separati. Il link è modellato in ConnectorLinks.
    //  Se vuoi navigazione cross-floor, usa ConnectorLinks per trovare il nodo
    //  ponte e poi il grafo del piano destinazione per il pathfinding locale.
    // ====================================================
   // Sostituisci l'intero metodo ExecuteFloorChange con questo:

private void ExecuteFloorChange(int targetFloor)
{
    if (state == State.Completed) return;

    int    fromFloor  = currentFloor;
    string connNodeId = string.Empty;

    if (pendingConnector != null)
    {
        traversedConnectors.Add(pendingConnector.id);

        // ── 1. Nome del nodo connettore = ID del connettore ───────────────
        connNodeId = pendingConnector.id;  // es. "scalaBox"
        Debug.Log($"[ExplMgr] Nodo connettore: '{connNodeId}'");

        // ── 2. Nodo connettore nel grafo del piano CORRENTE ───────────────
        if (graph != null && graph.GetNode(connNodeId) == null)
            graph.AddRoomNode(connNodeId, pendingConnector.triggerStartPosition);

        // ── 3. Crea grafo piano DESTINAZIONE se non esiste ────────────────
        if (!floorGraphs.ContainsKey(targetFloor))
            floorGraphs[targetFloor] = new TopologicalGraph();

        // ── 4. Nodo connettore nel grafo DESTINAZIONE ─────────────────────
        var destGraph = floorGraphs[targetFloor];
        if (destGraph.GetNode(connNodeId) == null)
            destGraph.AddRoomNode(connNodeId, pendingConnector.triggerEndPosition);

        // ── 5. ConnectorLink cross-floor ──────────────────────────────────
        ConnectorLinks.Add(new FloorConnectorLink
        {
            connectorId = pendingConnector.id,
            floorFrom   = fromFloor,
            nodeIdFrom  = connNodeId,
            posFrom     = pendingConnector.triggerStartPosition,
            floorTo     = targetFloor,
            nodeIdTo    = connNodeId,
            posTo       = pendingConnector.triggerEndPosition
        });

        Debug.Log($"[ExplMgr] ConnectorLink: " +
                  $"piano {fromFloor}/'{connNodeId}' ↔ piano {targetFloor}/'{connNodeId}'");
    }
    else if (!floorGraphs.ContainsKey(targetFloor))
    {
        floorGraphs[targetFloor] = new TopologicalGraph();
    }

    // ── 6. Passa al piano destinazione ────────────────────────────────────
    currentFloor     = targetFloor;
    pendingConnector = null;

    registeredRoomDoors.Clear();
    registeredTransitDoors.Clear();
    allCorridorDoors.Clear();

    var floorData = buildingGraph?.GetFloor(targetFloor);
    if (floorData != null)
    {
        navAgent.Warp(floorData.spawnPosition);

        string spawnId = floorData.spawnObject != null
            ? floorData.spawnObject.name
            : $"floor_{targetFloor}_entry";

        currentNodeId = spawnId;
        if (graph.GetNode(currentNodeId) == null)
            graph.AddRoomNode(currentNodeId, floorData.spawnPosition);

        graph.EnterNode(currentNodeId);

        // ── 7. Arco spawn ↔ connettore (es. box ↔ scalaBox) ──────────────
        // Senza questo i due nodi sarebbero scollegati nel grafo del piano 0.
        if (!string.IsNullOrEmpty(connNodeId) && graph.GetNode(connNodeId) != null)
        {
            float dist = Vector3.Distance(floorData.spawnPosition,
                                          graph.GetNode(connNodeId).position);
            graph.AddSegmentArc(spawnId, connNodeId, dist, connNodeId);
            Debug.Log($"[ExplMgr] Arco '{spawnId}' ↔ '{connNodeId}' dist={dist:F1}m");
        }
    }

    inRoomMode             = false;
    movingToPoleCorridorId = null;
    StartRotation360(isRoom: true);

    Debug.Log($"[ExplMgr] Piano {targetFloor} — nuova esplorazione. " +
              $"Grafi attivi: {floorGraphs.Count}  Link: {ConnectorLinks.Count}");
}

    // ====================================================
    // FINE ESPLORAZIONE
    // ====================================================
    private void OnAllFloorsCompleted()
    {
        explorationVisionCone?.SetExplorationMode(false);
        state = State.Completed;

        // Retrocompatibilità: trasmette il grafo del piano corrente.
        // Per trasmettere tutti i grafi, estendi BeliefTransmitter con:
        //   TransmitAllGraphs(AllFloorGraphs, ConnectorLinks)
        var bt = GetComponent<BeliefTransmitter>();
        if (bt != null)
        {
            var sortedFloors = new List<int>(floorGraphs.Keys);
            sortedFloors.Sort();
            foreach (int f in sortedFloors)
                bt.TransmitGraph(floorGraphs[f]);
        }

        Debug.Log($"[ExplMgr] Edificio esplorato. " +
                  $"Piani: {floorGraphs.Count}  Link: {ConnectorLinks.Count}");

        foreach (var link in ConnectorLinks)
            Debug.Log($"  ↔ '{link.connectorId}': " +
                      $"piano {link.floorFrom}/'{link.nodeIdFrom}' ↔ " +
                      $"piano {link.floorTo}/'{link.nodeIdTo}'");
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
            if (connector.triggerEndObject?.name   == doorName) return true;
        }
        return false;
    }
    private VerticalConnector FindConnectorForDoor(string doorName)
{
    if (buildingGraph == null) return null;
    foreach (var c in buildingGraph.connectors)
    {
        if (c.triggerStartObject?.name == doorName) return c;
        if (c.triggerEndObject?.name == doorName) return c;
    }
    return null;
}
}