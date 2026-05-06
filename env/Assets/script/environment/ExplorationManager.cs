using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// ExplorationManager.cs  — v2
// ============================================================
// Implementa l'algoritmo a 3 fasi del documento:
//
//  FASE 1: Transito A→B  (DoorSensor censisce le porte)
//  FASE 2: Ispezione sistematica B→A (orderBW) con ricorsione
//          → stanza semplice  : 360° + esplora archi (greedy navmesh)
//          → stanza complessa : trattata come mini-corridoio (TODO v3)
//  FASE 3: Cambio piano via BuildingGraph + connettori verticali
//
// STRUTTURA GRAFO (livello 1):
//   corridorId_A ──[central]──► corridorId_B
//   corridorId_A ──[fw_porta]──► porta         (distFromA, orderFW)
//   corridorId_B ──[bw_porta]──► porta         (distFromB, orderBW)
//
// STRUTTURA GRAFO (livello 2):
//   roomNode ──[room_porta]──► porta            (navMeshDist, greedy)
// ============================================================

public class ExplorationManager : MonoBehaviour
{
    [Header("Agente")]
    public NavMeshAgent navAgent;
    public DoorSensor   doorSensor;
    public VisionCone   visionCone;

    [Header("Macro-Grafo edificio")]
    public BuildingGraph buildingGraph;

    [Header("Piano iniziale")]
    public int startingFloor = 0;

    [Header("Impostazioni")]
    public float arrivalThreshold   = 0.5f;
    public float rotationSpeed      = 120f;
    public float inspectionDistance = 1.2f;
    // Offset dopo la porta quando si entra in una stanza
    public float enterOffset        = 1.5f;

    // --------------------------------------------------------
    // MACCHINA A STATI
    // --------------------------------------------------------
    private enum State
    {
        Idle,
        MovingToPole,       // si sta avvicinando al polo A visibile
        Transiting,         // transito A→B, DoorSensor attivo
        WaitingAtPoleB,     // arrivato a B, aspetta un frame per calcoli
        Rotating360,        // rotazione 360° (corridoio o stanza)
        MovingToDoor,       // si sposta verso la prossima porta
        InspectingDoor,     // davanti alla porta, decide se aprire
        EnteringRoom,       // si sta muovendo dentro la stanza
        InsideRoom,         // dentro la stanza, fa 360° e ispeziona
        Backtracking,       // torna al nodo con archi discovered
        MovingToConnector,  // va verso scala/ascensore
        Completed
    }

    private State            state               = State.Idle;
    private TopologicalGraph graph               = new TopologicalGraph();
    private string           currentNodeId;

    // Corridoio corrente durante il transito
    private string  transitCorridorId;
    private Vector3 poleAPos;
    private Vector3 poleBPos;

    // Polo in attesa di elaborazione
    private string  pendingCorridorId;
    private string  pendingPoleId;
    private Vector3 pendingPolePos;

    // Arco corrente che si sta ispezionando
    private GraphEdge currentEdge;

    // Rotazione 360°
    private float rotationAccum = 0f;
    public bool  inRoomMode    = false; // true = siamo dentro una stanza

    // Flag per evitare registrazioni duplicate
    private HashSet<string> registeredDoors = new HashSet<string>();

    private bool explorationStarted = false;
    private int  currentFloor;
    private VerticalConnector pendingConnector;

    // --------------------------------------------------------
    // EVENTI
    // --------------------------------------------------------
    void Awake()
    {
        if (buildingGraph != null)
    {
        foreach (var floor in buildingGraph.floors)
        {
            floor.explored = false;
        }
    }
        CorridorPoleScript.OnPoleTouched           += HandlePoleTouched;
        DoorSensor.OnDoorDiscovered                += HandleDoorDiscovered;
        VisionCone.OnCorridorPoleVisible           += HandleCorridorPoleVisible;
        VisionCone.OnDoorVisibleInRoom             += HandleDoorVisibleInRoom;
        VerticalConnectorScript.OnConnectorReached += HandleConnectorReached;
    }

    void OnDestroy()
    {
        CorridorPoleScript.OnPoleTouched           -= HandlePoleTouched;
        DoorSensor.OnDoorDiscovered                -= HandleDoorDiscovered;
        VisionCone.OnCorridorPoleVisible           -= HandleCorridorPoleVisible;
        VisionCone.OnDoorVisibleInRoom             -= HandleDoorVisibleInRoom;
        VerticalConnectorScript.OnConnectorReached -= HandleConnectorReached;
    }

    // --------------------------------------------------------
    // AVVIO
    // --------------------------------------------------------
public void StartExploration(string startNodeId, Vector3 startPos)
{
    if (explorationStarted) return;
    explorationStarted = true;
    currentFloor = startingFloor;

    visionCone?.SetExplorationMode(true);
    currentNodeId = startNodeId;
    
    graph.AddNode(startNodeId, NodeType.Room, startPos); 
    graph.EnterNode(startNodeId);

    // --- CORREZIONE CS0176 ---
    CorridorPoleScript[] allPoles = FindObjectsOfType<CorridorPoleScript>();
    foreach (var p in allPoles)
    {
        // Accedi all'evento tramite la CLASSE (CorridorPoleScript), non tramite 'p'
        CorridorPoleScript.OnPoleTouched -= HandlePoleTouched; 
        CorridorPoleScript.OnPoleTouched += HandlePoleTouched;
    }

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
    // NON interrompere se stiamo già andando a un polo o transitando
    if (state == State.MovingToPole || state == State.Transiting) return;
    
    // NON interrompere il giro 360
    if (state == State.Rotating360) return; 

    // Se siamo in IDLE o BACKTRACKING (o siamo appena passati da una porta di un corridoio)
    if (state == State.Idle || state == State.Backtracking) 
    {
        if (FindCorridorData(corridorId) == null) return;

        pendingCorridorId = corridorId;
        pendingPoleId     = poleId;
        pendingPolePos    = polePos;

        navAgent.isStopped = false;
        navAgent.SetDestination(polePos);
        state = State.MovingToPole;

        Debug.Log($"[ExplMgr] Polo {corridorId}/{poleId} individuato → mi avvicino.");
    }
}

    // --------------------------------------------------------
    // HANDLER: Polo fisicamente toccato (trigger collider)
    // --------------------------------------------------------
   private void HandlePoleTouched(string corridorId, string poleId, Vector3 polePos)
{
    // Log di emergenza per vedere se il segnale arriva al cervello
    Debug.Log($"[ExplMgr] Segnale fisico ricevuto da {corridorId}_{poleId}. Stato attuale: {state}");

    // Se siamo in transito e tocchiamo il polo finale (B)
    if (state == State.Transiting && corridorId == transitCorridorId)
    {
        pendingCorridorId = corridorId;
        pendingPoleId     = poleId;
        pendingPolePos    = polePos;
        ProcessPoleTouched();
        return;
    }
    
    // Se siamo appena entrati nel corridoio o ci stavamo avvicinando al polo
    // Accettiamo il tocco anche se siamo in stati "vicini" come Inspecting o MovingToDoor
    if (state != State.Transiting)
    {
        pendingCorridorId = corridorId;
        pendingPoleId     = poleId;
        pendingPolePos    = polePos;
        ProcessPoleTouched();
    }
}

    // --------------------------------------------------------
    // HANDLER: DoorSensor scopre una porta durante il transito
    // --------------------------------------------------------
    private void HandleDoorDiscovered(DoorVarcoScript door, string corridorId, int orderFW)
    {
        if (state != State.Transiting) return;
        if (corridorId != transitCorridorId) return;   // ← aggiungere anche questo!
        if (registeredDoors.Contains(door.gameObject.name)) return;
        

        registeredDoors.Add(door.gameObject.name);

        // ── Crea gli archi doppi FW e BW nel grafo (Livello 1) ──
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
        if (registeredDoors.Contains(door.gameObject.name)) return;

        registeredDoors.Add(door.gameObject.name);
        door.discovered = true;

        // Calcola distanza NavMesh reale per il greedy
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

    // ─── MovingToPole ────────────────────────────────────
    private void UpdateMovingToPole()
    {
        if (navAgent.pathPending) return;
        if (navAgent.remainingDistance < arrivalThreshold)
            ProcessPoleTouched();
    }

    // Logica comune polo A e polo B
    private void ProcessPoleTouched()
    {
        var corridorData = FindCorridorData(pendingCorridorId);
        if (corridorData == null) return;

        bool isFirstPole = (state != State.Transiting);

        if (isFirstPole)
        {
            // ── POLO A: avvia transito ──
            poleAPos = pendingPolePos;

            // Destinazione: l'altro polo
            Vector3 targetB = pendingPoleId == "1"
                ? corridorData.Pole2Position
                : corridorData.Pole1Position;
            poleBPos = targetB;

            // Crea nodi polo A e polo B nel grafo
            float centralLen = Vector3.Distance(poleAPos, poleBPos);
            var (nodeA, nodeB) = graph.AddCorridorPoles(
                pendingCorridorId, poleAPos, poleBPos, centralLen);

            // Entra nel nodo polo A
            string poleAId = $"{pendingCorridorId}_A";
            currentNodeId  = poleAId;
            graph.EnterNode(poleAId);

            transitCorridorId = pendingCorridorId;

            navAgent.isStopped = false;
            navAgent.SetDestination(targetB);
            doorSensor.StartScan(poleAPos, targetB, pendingCorridorId);
            inRoomMode = false;
            state      = State.Transiting;

            Debug.Log($"[ExplMgr] Transito {pendingCorridorId}: A={poleAPos} → B={targetB}");
        }
        else
        {
            // ── POLO B: fine transito ──
            doorSensor.StopScan();

            string poleBId = $"{pendingCorridorId}_B";
            currentNodeId  = poleBId;
            graph.EnterNode(poleBId);

            // Calcola orderBW per tutti gli archi BW del polo B
            graph.ComputeBackwardOrders(pendingCorridorId);

            // Marca l'arco centrale A→B come explored
            string poleAId    = $"{pendingCorridorId}_A";
            string centralId  = $"central_{pendingCorridorId}";
            graph.MarkEdgeExplored(poleAId, centralId, poleBId);

            Debug.Log($"[ExplMgr] Polo B toccato. Inizio ispezione {pendingCorridorId}.");

            // Rotazione 360° al polo B prima di iniziare ispezione
            StartRotation360(isRoom: false);
        }

        pendingCorridorId = null;
        pendingPoleId     = null;
    }

    // ─── Transiting ──────────────────────────────────────
    private void UpdateTransiting()
    {
        // Il DoorSensor fa il lavoro; aspettiamo solo il polo B fisico.
        // Fallback: se navAgent è arrivato ma il trigger non è scattato
        if (navAgent.pathPending) return;
        if (navAgent.remainingDistance < arrivalThreshold * 0.5f)
        {
            // Simula polo B raggiunto
            pendingCorridorId = transitCorridorId;
            pendingPoleId     = "B_fallback";
            pendingPolePos    = poleBPos;
            state             = State.Transiting; // mantieni per isFirstPole=false
            ProcessPoleTouched();
        }
    }

    // ─── Rotating360 ─────────────────────────────────────
private void StartRotation360(bool isRoom)
{
    inRoomMode = isRoom;
    // --- AGGIUNTA FONDAMENTALE ---
    // Se siamo in un corridoio (isRoom = false), forziamo il VisionCone 
    // a scansionare i varchi ANCHE se non siamo in una stanza.
    if (!isRoom) {
        inRoomMode = true; // Lo attiviamo temporaneamente per il giro di ricognizione
    }
    
    rotationAccum = 0f;
    navAgent.isStopped = true;
    state = State.Rotating360;
    Debug.Log($"[ExplMgr] 360° in '{currentNodeId}' (forced_room_mode for inspection)");
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

    // ─── MovingToDoor ─────────────────────────────────────
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

    // ─── InspectingDoor ───────────────────────────────────
    private void UpdateInspectingDoor()
    {
        if (currentEdge == null) { DecideNextAction(); return; }

        // Porta bloccata → segna explored e passa alla prossima
        if (currentEdge.doorState == DoorState.Locked)
        {
            graph.MarkEdgeExplored(currentNodeId, currentEdge.id, null);
            currentEdge = null;
            navAgent.isStopped = false;
            DecideNextAction();
            return;
        }

        // Prova ad aprire se è una porta fisica
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

    // ─── EnteringRoom ─────────────────────────────────────
    // FIX 4: aspetta che l'agente sia fisicamente dentro prima del 360°
    private void UpdateEnteringRoom()
    {
        if (navAgent.pathPending) return;
        if (navAgent.remainingDistance < arrivalThreshold)
        {
            // Ora siamo dentro: avvia 360° in modalità stanza
            StartRotation360(isRoom: true);
        }
    }

    // ─── InsideRoom ───────────────────────────────────────
    // Dopo il 360° interno → DecideNextAction sceglie greedy
    private void UpdateInsideRoom()
    {
        // Questo stato è usato solo come "wrapper" dopo EnteringRoom.
        // La logica vera è in DecideNextAction → NextRoomEdgeToInspect.
        // Se arriviamo qui senza 360°, lo avviamo.
        StartRotation360(isRoom: true);
    }

    // ─── Backtracking ─────────────────────────────────────
private void UpdateBacktracking()
{
    // Rimuovi o commenta questa riga per vedere se entra nella funzione
    // if (navAgent.pathPending) return; 

    // Aumentiamo la soglia a 0.8 metri. 
    // Spesso il NavMeshAgent non arriva a 0.5 se c'è poco spazio.
    if (navAgent.remainingDistance <= 0.8f || !navAgent.hasPath) 
    {
        currentNodeId = graph.CurrentNodeId; 
        
        if (string.IsNullOrEmpty(currentNodeId))
        {
            CheckFloorCompletion();
            return;
        }

        Debug.Log($"[ExplMgr] Backtrack FORZATO in {currentNodeId}. Prossima azione!");
        state = State.Idle;
        DecideNextAction(); 
    }
}
    // ─── MovingToConnector ────────────────────────────────
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
        string targetNode = null;
        GraphNode targetData = null;

        // Srotoliamo il "Filo di Arianna" nello stack finché non troviamo un nodo con porte
        while (true)
        {
            targetNode = graph.Backtrack(); // Fa pop e guarda il precedente
            
            if (targetNode == null)
            {
                // Stack vuoto, non ci sono più nodi da esplorare. Piano completato!
                CheckFloorCompletion();
                return;
            }

            targetData = graph.GetNode(targetNode);
            
            // Se il nodo trovato ha ancora archi 'Discovered' da esplorare, usciamo dal loop!
            if (targetData != null && targetData.HasUndiscoveredEdges())
            {
                break; 
            }
            
            Debug.Log($"[ExplMgr] Il nodo {targetNode} è già tutto esplorato, riavvolgo ancora...");
        }

        // Abbiamo trovato un nodo utile (es. corridoio2_B con la cucina)
        navAgent.isStopped = false;
        navAgent.SetDestination(targetData.position);
        state = State.Backtracking;
        Debug.Log($"[ExplMgr] Backtrack fisico → vado verso {targetNode} per riprendere l'esplorazione.");
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
        // 1. Prova prima la logica del corridoio (porte censite dal DoorSensor)
        nextEdge = graph.NextCorridorEdgeToInspect(currentNodeId);
        
        // 2. SE NON TROVA NULLA, prova la logica delle stanze (varchi visti col 360°)
        if (nextEdge == null) 
        {
            nextEdge = graph.NextRoomEdgeToInspect(currentNodeId);
            Debug.Log("[ExplMgr] Nessuna porta BW trovata, provo varchi visti col 360°.");
        }
    }
    else if (node.type == NodeType.Room || node.type == NodeType.Complex)
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
    // ENTRA ATTRAVERSO UN ARCO (porta/varco)
    // ====================================================
  // ====================================================
    // ENTRA ATTRAVERSO UN ARCO (porta/varco)
    // ====================================================
    private void EnterThroughEdge(GraphEdge edge)
    {
        var doorScript = FindDoorScript(edge.id);

        // Determiniamo subito il nome della stanza oltre la porta
        string newNodeId = doorScript != null
            ? doorScript.GetRoomNameBeyondDoor(transform.position)
            : edge.id;

        bool isTargetCorridor = newNodeId.ToLower().Contains("corridoio");

        // --- NUOVA LOGICA ANTI-LOOP UNIFICATA ---
        var existingNode = graph.GetNode(newNodeId);
        
        // Se il nodo esiste già ed è completato (e non è un corridoio)...
        if (existingNode != null && !existingNode.HasUndiscoveredEdges() && !isTargetCorridor)
        {
            Debug.LogWarning($"[ExplMgr] LOOP EVITATO: La stanza '{newNodeId}' è già completata. Passo oltre.");
            graph.MarkEdgeExplored(currentNodeId, edge.id, newNodeId);
            SyncTwinCorridorEdge(edge.id, newNodeId);
            
            currentEdge = null;
            navAgent.isStopped = false;
            DecideNextAction(); 
            return;
        }

        // --- PROSEGUIMENTO INGRESSO NORMALE ---
        
        // Marca l'arco come esplorato e sincronizza i gemelli dei corridoi
        graph.MarkEdgeExplored(currentNodeId, edge.id, isTargetCorridor ? null : newNodeId);
        SyncTwinCorridorEdge(edge.id, newNodeId);

        // SE È UN CORRIDOIO: Non creiamo il nodo fittizio "Room"! 
        // Attraversiamo la porta e lasciamo che VisionCone/Sensor trovino i Poli A e B.
        if (isTargetCorridor)
        {
            Debug.Log($"[ExplMgr] Rilevato transito verso un corridoio '{newNodeId}'. Evito la creazione del nodo Room.");
            state = State.Idle; 
        }
        else
        {
            // Se è una stanza normale, la aggiungiamo al grafo come sempre
            graph.AddNode(newNodeId, NodeType.Room, edge.position);
            currentNodeId = newNodeId;
            graph.EnterNode(currentNodeId);
            state = State.EnteringRoom; 
        }

        // Calcolo punto di ingresso fisico
        Vector3 doorNormal = doorScript != null ? doorScript.transform.forward : transform.forward;
        Vector3 toDoor = (edge.position - transform.position).normalized;
        if (Vector3.Dot(doorNormal, toDoor) < 0) { doorNormal = -doorNormal; }
        
        Vector3 enterTarget = edge.position + doorNormal * enterOffset;
        
        navAgent.isStopped = false;
        navAgent.SetDestination(enterTarget);
        currentEdge = null;

        Debug.Log($"[ExplMgr] Spostamento fisico oltre la porta verso: {newNodeId}");
    }

    // Helper per sincronizzare FW e BW nei corridoi: evita che il Polo A ti faccia ripetere le porte del Polo B
    private void SyncTwinCorridorEdge(string edgeId, string toNodeId)
    {
        if (string.IsNullOrEmpty(currentNodeId)) return;

        if (currentNodeId.EndsWith("_B") && edgeId.StartsWith("bw_")) {
            string poleA = currentNodeId.Replace("_B", "_A");
            string fwEdge = edgeId.Replace("bw_", "fw_");
            graph.MarkEdgeExplored(poleA, fwEdge, toNodeId);
        }
        else if (currentNodeId.EndsWith("_A") && edgeId.StartsWith("fw_")) {
            string poleB = currentNodeId.Replace("_A", "_B");
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

    private bool IsCurrentFloorFullyExplored(FloorNode floorData)
    {
        return graph.IsFullyExplored();
    }

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
        registeredDoors.Clear();
        graph = new TopologicalGraph();

        var floorData = buildingGraph?.GetFloor(targetFloor);
        if (floorData != null)
        {
            navAgent.Warp(floorData.spawnPosition);
            currentNodeId = $"floor_{targetFloor}_entry";
            graph.AddNode(currentNodeId, NodeType.Unknown, floorData.spawnPosition);
            graph.EnterNode(currentNodeId);
        }

        inRoomMode = false;
        StartRotation360(isRoom: false);
        Debug.Log($"[ExplMgr] Piano {targetFloor} — nuova esplorazione.");
    }

    private void OnAllFloorsCompleted()
    {
        visionCone?.SetExplorationMode(false);
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
        // Rimuove prefissi aggiunti dal grafo (fw_, bw_, room_)
        string cleanName = name;
        if (name.StartsWith("fw_"))   cleanName = name.Substring(3);
        if (name.StartsWith("bw_"))   cleanName = name.Substring(3);
        if (name.StartsWith("room_")) cleanName = name.Substring(5);
        return GameObject.Find(cleanName)?.GetComponent<DoorVarcoScript>();
    }
}