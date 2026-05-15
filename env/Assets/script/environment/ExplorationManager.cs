using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using System.Collections;  

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


    [Header("Impostazioni")]

    public float arrivalThreshold      = 0.5f;
    public float poleArrivalThreshold  = 1.2f;
    public float rotationSpeed         = 120f;
    public float inspectionDistance    = 1.2f;
    public float enterOffset           = 1.5f;
    public float poleTimeoutSeconds    = 4f;

    [Header("Discesa Scale")]
    [Tooltip("Waypoint dell'animazione, dalla cima al fondo della scala.")]
    public Transform[] stairsWaypoints;
    public float stairsMoveSpeed = 1.2f;

    [Header("Animatore")]
    public Animator agentAnimator;
    public string walkAnimParam = "IsWalking"; // oppure "Speed" se usi blend tree

    // --------------------------------------------------------
    // MACCHINA A STATI
    // --------------------------------------------------------
    private enum State
    {
        Idle, MovingToPole, Transiting, WaitingAtPoleB,
        Rotating360, MovingToDoor, InspectingDoor,
        EnteringRoom, InsideRoom, Backtracking,
        MovingToConnector, Completed,TraversingStairs
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
    public int  currentFloor;
    private VerticalConnector pendingConnector;
    private HashSet<string>   traversedConnectors = new HashSet<string>();

    private string movingToPoleCorridorId;
    private float  movingToPoleTimer = 0f;
    private float  backtrackTimer    = 0f;
    public  float  backtrackTimeout  = 5f;

    private float transitTimer = 0f;

    // Corridoio verso cui l'agente sta navigando in stato Idle
    // (dopo EnterThroughEdge su un varco corridoio). Blocca segnali
    // di poli di altri corridoi finché non si tocca il polo corretto.
    private string idleTargetCorridorId;

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
        if (buildingGraph == null) { Debug.LogWarning("[ExplMgr] BuildingGraph non assegnato!"); return; }

        // Trova il piano cercando quale ha un roomObject vicino all'agente
        Vector3 agentPos = navAgent.transform.position;
        FloorNode floorData = null;
        float minDist = float.MaxValue;

        foreach (var floor in buildingGraph.floors)
        {
            foreach (var room in floor.roomObjects)
            {
                if (room == null) continue;
                float d = Vector3.Distance(agentPos, room.transform.position);
                if (d < minDist) { minDist = d; floorData = floor; }
            }
        }

        if (floorData == null || floorData.spawnObject == null)
        { Debug.LogWarning("[ExplMgr] Piano non trovato o spawnObject mancante!"); return; }

        Vector3 startPos = floorData.spawnPosition;

        if (Vector3.Distance(agentPos, startPos) > 0.05f)
        {
            Debug.Log($"[ExplMgr] Teleport allo spawn del piano {floorData.floorIndex}");
            navAgent.enabled = false;
            navAgent.transform.position = startPos;
            navAgent.enabled = true;
            navAgent.Warp(startPos);
        }
        currentFloor = floorData.floorIndex;  // ← aggiungere questa riga
        StartExploration(floorData.spawnObject.name, startPos);
    }
    // ──────────────────────────────────────────────────────────────
// METODO 1: StartExploration  (sostituisce l'intero metodo)
// ──────────────────────────────────────────────────────────────
public void StartExploration(string startNodeId, Vector3 startPos)
{
    if (explorationStarted) return;
    explorationStarted = true;
 

    Debug.Log($"[ExplMgr] Piano di partenza: {currentFloor}");
 
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
        //if (!string.IsNullOrEmpty(idleTargetCorridorId) && corridorId != idleTargetCorridorId) return;

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
            // Se stiamo navigando verso un corridoio specifico (idleTargetCorridorId),
            // ignora segnali di polo di altri corridoi per non interrompere il tragitto.
            if (!string.IsNullOrEmpty(idleTargetCorridorId) && corridorId != idleTargetCorridorId)
            {
                Debug.Log($"[ExplMgr] Polo {corridorId}_{poleId} ignorato — in rotta verso '{idleTargetCorridorId}'.");
                ResetCorridorPoles(corridorId);
                return;
            }

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

            // Corridoio corretto raggiunto: pulisci il target idle
            idleTargetCorridorId = null;
            pendingCorridorId = corridorId;
            pendingPoleId     = poleId;
            pendingPolePos    = polePos;
            ProcessPoleTouched();
            return;
        }

        Debug.Log($"[ExplMgr] Polo {corridorId}_{poleId} ignorato — stato={state}.");
        ResetCorridorPoles(corridorId); // ← aggiunto
    }

    // --------------------------------------------------------
    // HANDLER: DoorSensor scopre una porta durante il transito
    // --------------------------------------------------------
    private void HandleDoorDiscovered(DoorVarcoScript door, string corridorId, int orderFW)
    {
        if (state != State.Transiting) return;
        if (corridorId != transitCorridorId) return;
        if (registeredTransitDoors.Contains(door.gameObject.name)) return;
        float corridorLength = Vector3.Distance(poleAPos, poleBPos);
    if (door.distFromB > corridorLength + 0.3f && door.distFromA > corridorLength + 0.3f )  // 0.3m di tolleranza
    {
        Debug.Log($"[ExplMgr] {door.gameObject.name} oltre il corridoio " +
                  $"(dB={door.distFromB:F2} > len={corridorLength:F2}) → ignorata");
        return;
    }

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
        if (traversedConnectors.Contains(currentNodeId) && !IsConnectorDoor(door.gameObject.name))
        {
            Debug.Log($"[ExplMgr] {door.gameObject.name} ignorata — siamo in nodo connettore '{currentNodeId}'");
            return;
        }

        // Le porte connettore (scale/ascensori) vengono ignorate nel 360°.
        // L'agente non aggiungerà archi verso di esse durante l'esplorazione
        // del piano corrente — verranno usate solo da CheckFloorCompletion.
        if (IsConnectorDoor(door.gameObject.name))
        {
            // Se questo connettore è già stato attraversato (es. le scale usate per salire),
            // aggiungiamo subito un RoomDoor già explored: non va ri-analizzato.
            var connectorForDoor = FindConnectorForDoor(door.gameObject.name);
            if (connectorForDoor != null && traversedConnectors.Contains(connectorForDoor.id))
            {
                if (!registeredRoomDoors.Contains(door.gameObject.name))
                {
                    registeredRoomDoors.Add(door.gameObject.name);
                   string connRoomBeyond = door.GetRoomNameBeyondDoor(transform.position);
                    string targetId = connRoomBeyond;

                    // Se la stanza non esiste ancora nel grafo, creala subito da BuildingGraph
                    if (!string.IsNullOrEmpty(targetId) && graph.GetNode(targetId) == null)
                    {
                        var floorData = buildingGraph?.GetFloor(currentFloor);
                        var roomObj = floorData?.roomObjects.Find(r => r != null && r.name == targetId);
                        if (roomObj != null)
                            graph.AddRoomNode(targetId, roomObj.transform.position);
                    }

                    if (string.IsNullOrEmpty(targetId) || graph.GetNode(targetId) == null)
                        targetId = connectorForDoor.id;
                    float connNavDist = door.NavMeshDistanceTo(transform.position);
                    var edge = graph.AddRoomDoorEdge(currentNodeId, door.gameObject.name,
                        door.elementType == DoorVarcoScript.ElementType.Door,
                        door.transform.position, connNavDist, targetId);
                    if (edge != null) graph.MarkEdgeExplored(currentNodeId, edge.id, targetId);
                    Debug.Log($"[ExplMgr] Connettore '{door.gameObject.name}' già attraversato → RoomDoor explored verso '{targetId}'.");
                }
                return;
            }

            string corridorBeyond = door.GetRoomNameBeyondDoor(transform.position);
            if (!string.IsNullOrEmpty(corridorBeyond) && corridorBeyond.ToLower().Contains("corridoio"))
            {
                if (!registeredRoomDoors.Contains(door.gameObject.name))
                {
                    registeredRoomDoors.Add(door.gameObject.name);
                    NavMeshPath path = new NavMeshPath();
                    float corridorNavDist = 0f;
                    if (NavMesh.CalculatePath(transform.position, door.transform.position, NavMesh.AllAreas, path))
                        for (int i = 1; i < path.corners.Length; i++)
                            corridorNavDist += Vector3.Distance(path.corners[i-1], path.corners[i]);
                    graph.AddRoomDoorEdge(currentNodeId, door.gameObject.name,
                        door.elementType == DoorVarcoScript.ElementType.Door,
                        door.transform.position, corridorNavDist, corridorBeyond);
                    Debug.Log($"[ExplMgr] Porta connettore registrata come varco corridoio: {door.gameObject.name} → {corridorBeyond}");
                }
            }
            else
            {
                Debug.Log($"[ExplMgr] {door.gameObject.name} è porta connettore → ignorata nel 360°");
            }
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

            if (dB > 0.6f && dA>0.6)
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

        // DOPO:
        string roomBeyond = door.GetRoomNameBeyondDoor(transform.position);
        bool leadsToCorridor = !string.IsNullOrEmpty(roomBeyond)
                            && roomBeyond.ToLower().Contains("corridoio");

        float navDist = door.NavMeshDistanceTo(transform.position);
        graph.AddRoomDoorEdge(currentNodeId, door.gameObject.name,
            door.elementType == DoorVarcoScript.ElementType.Door,
            door.transform.position, navDist,
            toNodeId: leadsToCorridor ? roomBeyond : null);

        if (leadsToCorridor)
            allCorridorDoors.Add(door.gameObject.name);

        Debug.Log($"[ExplMgr] Porta in stanza: {door.gameObject.name} navDist={navDist:F1}" +
                (leadsToCorridor ? $" → corridoio '{roomBeyond}'" : ""));
    }

    // --------------------------------------------------------
    // HANDLER: connettore verticale raggiunto (trigger fisico)
    // --------------------------------------------------------
    private void HandleConnectorReached(string connectorId, int targetFloor, Vector3 pos)
{
    if (state != State.MovingToConnector) return;
    if (pendingConnector == null || pendingConnector.id != connectorId) return;

    bool isReverse = (pendingConnector.floorFrom == targetFloor);
    Vector3 connPosHere = isReverse
        ? pendingConnector.triggerEndPosition
        : pendingConnector.triggerStartPosition;

    int fromFloor = isReverse ? pendingConnector.floorTo : pendingConnector.floorFrom;
    floorGraphs.TryGetValue(fromFloor, out var fromGraph);

    if (fromGraph != null && fromGraph.GetNode(connectorId) == null)
    {
        fromGraph.AddRoomNode(connectorId, connPosHere);
        string sourceId = FindNearestRoomNodeIn(fromGraph, connPosHere);
        if (!string.IsNullOrEmpty(sourceId))
        {
            float dist = Vector3.Distance(fromGraph.GetNode(sourceId).position, connPosHere);
            var edge = fromGraph.AddRoomDoorEdge(sourceId, connectorId, true, connPosHere, dist, connectorId);
            if (edge != null) fromGraph.MarkEdgeExplored(sourceId, edge.id, connectorId);
        }
        fromGraph.EnterNode(connectorId);
    }

    if (stairsWaypoints != null && stairsWaypoints.Length > 0)
    {
       bool ascending = isReverse; // isReverse = (pendingConnector.floorFrom == targetFloor)
state = State.TraversingStairs;
StartCoroutine(TraverseStairsThen(targetFloor, ascending));
    }
    else
    {
        ExecuteFloorChange(targetFloor);
    }
}
    private string FindNearestRoomNodeIn(TopologicalGraph g, Vector3 pos)
    {
        string best = null;
        float bestDist = float.MaxValue;
        foreach (var node in g.AllNodes())
        {
            if (node == null || node.type != NodeType.Room) continue;
            if (node.id == pendingConnector?.id) continue; // salta il connettore stesso
            float d = Vector3.Distance(node.position, pos);
            if (d < bestDist) { bestDist = d; best = node.id; }
        }
        return best;
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
        transitTimer += Time.deltaTime;

        if (navAgent.pathPending && transitTimer < poleTimeoutSeconds) return;

        if (navAgent.remainingDistance < poleArrivalThreshold || transitTimer >= poleTimeoutSeconds)
        {
            transitTimer = 0f;
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

        // Marca il nodo come fisicamente visitato (360° reale).
        // Questo distingue i nodi aggiunti programmaticamente (es. ExecuteFloorChange)
        // dai nodi realmente esplorati, impedendo che LOOP EVITATO li salti.
        if (isRoom && graph != null)
        {
            var visitNode = graph.GetNode(currentNodeId);
            if (visitNode != null) visitNode.physicallyVisited = true;
        }

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
    {
        int targetFloor = (pendingConnector.floorFrom == currentFloor)
            ? pendingConnector.floorTo
            : pendingConnector.floorFrom;
        bool ascending = (pendingConnector.floorFrom != currentFloor);

        if (stairsWaypoints != null && stairsWaypoints.Length > 0)
        {
            state = State.TraversingStairs;
            StartCoroutine(TraverseStairsThen(targetFloor, ascending));
        }
        else
        {
            ExecuteFloorChange(targetFloor);
        }
    }
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
        if (existingNode != null && !existingNode.HasUndiscoveredEdges() && !isTargetCorridor && existingNode.physicallyVisited)
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

            // Rimuovi il RoomDoor che puntava a questo varco dal nodo corrente
            // perché ora sappiamo che è un link corridoio, non una porta stanza
            var currentNode = graph.GetNode(currentNodeId);
            if (currentNode != null)
            {
                currentNode.edges.RemoveAll(e =>
                    e != null &&
                    e.edgeType == EdgeType.Door &&
                    e.toNodeId == edge.toNodeId);
                Debug.Log($"[ExplMgr] Rimosso RoomDoor '{edge.toNodeId}' da '{currentNodeId}' — è un link corridoio");
            }

            if (graph.IsCorridorFullyTransited(newNodeId))
            {
                Debug.LogWarning($"[ExplMgr] Corridoio '{newNodeId}' già transitato.");
                currentEdge = null;
                navAgent.isStopped = false;
                DecideNextAction();
                return;
            }
            Debug.Log($"[ExplMgr] Transito verso corridoio '{newNodeId}'.");
            idleTargetCorridorId = newNodeId; // blocca poli di altri corridoi durante il tragitto
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

        var unexplored = buildingGraph.GetUnexploredFloors();
        unexplored.Sort((a, b) => a.floorIndex.CompareTo(b.floorIndex));

        foreach (var targetFloor in unexplored)
        {
            var connectors = buildingGraph.GetConnectors(currentFloor, targetFloor.floorIndex);
            connectors.RemoveAll(c => traversedConnectors.Contains(c.id));
            if (connectors.Count == 0) continue;

            connectors.Sort((a, b) => a.costUp.CompareTo(b.costUp));
            pendingConnector = connectors[0];

            bool isReverse = (pendingConnector.floorFrom != currentFloor);
            Vector3 navTarget = isReverse
                ? pendingConnector.triggerEndPosition
                : pendingConnector.triggerStartPosition;

            Debug.Log($"[ExplMgr] Piano {currentFloor} → piano {targetFloor.floorIndex} via '{pendingConnector.id}' (reverse={isReverse})");
            navAgent.isStopped = false;
            navAgent.SetDestination(navTarget);
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
    // Cerca stanze non ancora nel grafo (es. box quando si inizia dal piano 1)
    if (floorData != null)
    {
        foreach (string roomId in floorData.roomIds)
        {
            if (graph.GetNode(roomId) != null) continue;
            if (graph.GetCorridorPoleNode($"{roomId}_A") != null) continue;

            var roomObj = floorData.roomObjects.Find(r => r != null && r.name == roomId);
            if (roomObj == null) continue;

            Debug.Log($"[ExplMgr] Navigazione verso stanza non visitata: {roomId}");
            // Aggiungi il nodo e EnterNode così il 360° parte correttamente
            graph.AddRoomNode(roomId, roomObj.transform.position);
            graph.EnterNode(roomId);
            // Corregge archi connettore che puntano a se stessi (toNodeId == fromNodeId)
            // causati dal fatto che la stanza non era ancora nel grafo
            foreach (var node in graph.AllNodes())
            {
                if (node == null) continue;
                foreach (var edge in node.edges)
                {
                    if (edge != null && edge.toNodeId == node.id && edge.state == EdgeState.Explored)
                    {
                        edge.toNodeId = roomId;
                        Debug.Log($"[ExplMgr] Arco {node.id}→{edge.id} corretto: toNodeId aggiornato a '{roomId}'");
                    }
                }
            }
            currentNodeId = roomId;
            navAgent.isStopped = false;
            navAgent.SetDestination(roomObj.transform.position);
            state = State.EnteringRoom;
            return;
        }
    }

    // Fallback originale
    string targetId = graph.FindNearestNodeWithDiscoveredEdges(transform.position);
    if (targetId == null) { OnAllFloorsCompleted(); return; }

    navAgent.SetDestination(graph.GetNode(targetId).position);
    currentNodeId = targetId;
    state = State.Backtracking;
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

    int fromFloor = currentFloor;
    string connNodeId = string.Empty;
    var activeConnector = pendingConnector;
    bool isReverse = (activeConnector != null && activeConnector.floorFrom == targetFloor);

    if (activeConnector != null)
    {
        traversedConnectors.Add(activeConnector.id);
        connNodeId = activeConnector.id;

        Vector3 connPosCurrentFloor = isReverse
            ? activeConnector.triggerEndPosition
            : activeConnector.triggerStartPosition;

        Vector3 connPosDestFloor = isReverse
            ? activeConnector.triggerStartPosition
            : activeConnector.triggerEndPosition;

        if (graph != null && graph.GetNode(connNodeId) == null)
            graph.AddRoomNode(connNodeId, connPosCurrentFloor);

        if (!floorGraphs.ContainsKey(targetFloor))
            floorGraphs[targetFloor] = new TopologicalGraph();

        var destGraph = floorGraphs[targetFloor];
        if (destGraph.GetNode(connNodeId) == null)
            destGraph.AddRoomNode(connNodeId, connPosDestFloor);

        ConnectorLinks.Add(new FloorConnectorLink
        {
            connectorId = activeConnector.id,
            floorFrom   = fromFloor,
            nodeIdFrom  = connNodeId,
            posFrom     = connPosCurrentFloor,
            floorTo     = targetFloor,
            nodeIdTo    = connNodeId,
            posTo       = connPosDestFloor
        });

        Debug.Log($"[ExplMgr] ConnectorLink: piano {fromFloor}/'{connNodeId}' ↔ piano {targetFloor}/'{connNodeId}'");
    }
    else if (!floorGraphs.ContainsKey(targetFloor))
    {
        floorGraphs[targetFloor] = new TopologicalGraph();
    }

    currentFloor     = targetFloor;
    pendingConnector = null;

    registeredRoomDoors.Clear();
    registeredTransitDoors.Clear();
    allCorridorDoors.Clear();

    var floorData = buildingGraph?.GetFloor(targetFloor);
    if (floorData != null)
    {
        Vector3 warpTarget = (activeConnector != null)
            ? (isReverse ? activeConnector.triggerStartPosition : activeConnector.triggerEndPosition)
            : floorData.spawnPosition;

        // Warp solo se NON stiamo scendendo fisicamente le scale
        if (state != State.TraversingStairs)
        {
            navAgent.enabled = false;
            navAgent.transform.position = warpTarget;
            navAgent.enabled = true;
        }
        navAgent.Warp(warpTarget);

        string spawnId = !string.IsNullOrEmpty(connNodeId)
            ? connNodeId
            : (floorData.spawnObject != null ? floorData.spawnObject.name : $"floor_{targetFloor}_entry");

        currentNodeId = spawnId;
        if (graph.GetNode(currentNodeId) == null)
            graph.AddRoomNode(currentNodeId, floorData.spawnPosition);

        graph.EnterNode(currentNodeId);

        // NON si crea un arco segment spawn→connettore.
    }

    inRoomMode             = false;
    movingToPoleCorridorId = null;
    idleTargetCorridorId   = null;

    CorridorData spawnCorridor = FindCorridorContainingPosition(navAgent.transform.position, 3f);
    if (spawnCorridor != null)
    {
        Debug.Log($"[ExplMgr] Spawn in corridoio '{spawnCorridor.corridorId}' — avvio modalità corridoio.");
        pendingCorridorId      = spawnCorridor.corridorId;
        pendingPoleId          = "1";
        pendingPolePos         = spawnCorridor.Pole1Position;
        movingToPoleCorridorId = spawnCorridor.corridorId;
        navAgent.isStopped     = false;
        navAgent.SetDestination(spawnCorridor.Pole1Position);
        movingToPoleTimer      = 0f;
        state                  = State.MovingToPole;
    }
    else
    {
        StartRotation360(isRoom: true);
    }
}
    // ====================================================
    // FINE ESPLORAZIONE
    // ====================================================

    private void OnAllFloorsCompleted()
{
    explorationVisionCone?.SetExplorationMode(false);
    state = State.Completed;

    var bt = GetComponent<BeliefTransmitter>();
    if (bt != null)
    {
        // 1. Trasmetti i grafi per piano (già esistente)
        var sortedFloors = new List<int>(floorGraphs.Keys);
        sortedFloors.Sort();
        foreach (int f in sortedFloors)
            bt.TransmitGraph(floorGraphs[f]);

        // 2. Calcola e trasmetti le distanze porte (NUOVO)
        var ddm = GetComponent<DoorDistanceMap>();
        if (ddm != null)
        {
            ddm.Compute(floorGraphs);
            bt.TransmitDoorDistances(ddm.pairs);
        }
        else
        {
            Debug.LogWarning("[ExplMgr] DoorDistanceMap non trovato sul GameObject!");
        }
    }

    Debug.Log($"[ExplMgr] Edificio esplorato. Piani: {floorGraphs.Count} Link: {ConnectorLinks.Count}");
}
   /* VECCHIO
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
*/
    // ====================================================
    // HELPER
    // ====================================================
    private CorridorData FindCorridorData(string id)
    {
        foreach (var c in FindObjectsByType<CorridorData>(FindObjectsSortMode.None))
            if (c.corridorId == id) return c;
        return null;
    }
        private CorridorData FindCorridorContainingPosition(Vector3 pos, float radius)
    {
        foreach (var c in FindObjectsByType<CorridorData>(FindObjectsSortMode.None))
        {
            // La posizione è tra i due poli (proiezione sull'asse del corridoio)
            Vector3 axis = c.Pole2Position - c.Pole1Position;
            float len    = axis.magnitude;
            if (len < 0.01f) continue;
            Vector3 dir  = axis / len;
            float proj   = Vector3.Dot(pos - c.Pole1Position, dir);
            // Distanza laterale dall'asse
            Vector3 onAxis  = c.Pole1Position + dir * Mathf.Clamp(proj, 0f, len);
            float lateralDist = Vector3.Distance(pos, onAxis);
            if (lateralDist <= radius && proj >= -radius && proj <= len + radius)
                return c;
        }
        return null;
    }

    private DoorVarcoScript FindDoorScript(string name)
    {
        string cleanName = name;
        if (name.StartsWith("fw_"))   cleanName = name.Substring(3);
        if (name.StartsWith("bw_"))   cleanName = name.Substring(3);
        //if (name.StartsWith("room_")) cleanName = name.Substring(5);

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

// Aggiungi questo helper:
private void ResetCorridorPoles(string corridorId)
{
    foreach (var pole in FindObjectsByType<CorridorPoleScript>(FindObjectsSortMode.None))
    {
        if (pole.corridorId == corridorId)
            pole.ResetTrigger();
    }
}

//DISCESA SCALE
private IEnumerator TraverseStairsThen(int targetFloor, bool ascending)
{
    var activeConnector = pendingConnector;

    navAgent.isStopped = true;
    navAgent.enabled   = false;

    if (agentAnimator != null)
        agentAnimator.SetBool(walkAnimParam, true);

    // ascending=true → waypoint in ordine inverso (fondo→cima)
    int start = ascending ? stairsWaypoints.Length - 1 : 0;
    int end   = ascending ? -1 : stairsWaypoints.Length;
    int step  = ascending ? -1 : 1;

    for (int i = start; i != end; i += step)
    {
        Transform wp = stairsWaypoints[i];

        Vector3 dir = (wp.position - transform.position);
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir.normalized);
            float elapsed = 0f;
            while (elapsed < 0.25f)
            {
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, targetRot, elapsed / 0.25f);
                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        while (Vector3.Distance(transform.position, wp.position) > 0.05f)
        {
            transform.position = Vector3.MoveTowards(
                transform.position, wp.position, stairsMoveSpeed * Time.deltaTime);
            yield return null;
        }

        transform.position = wp.position;
    }

    if (agentAnimator != null)
        agentAnimator.SetBool(walkAnimParam, false);

    // Apre la porta giusta: cima se salita, fondo se discesa
    var arrivalDoor = ascending
        ? activeConnector?.triggerStartObject
        : activeConnector?.triggerEndObject;

    if (arrivalDoor != null)
    {
        var door = arrivalDoor.GetComponentInChildren<DoorVarcoScript>();
        if (door != null && !door.IsTraversable)
        {
            door.TryOpen();
            yield return new WaitForSeconds(0.6f);
        }
    }

    navAgent.enabled = true;
    ExecuteFloorChange(targetFloor);
}
}