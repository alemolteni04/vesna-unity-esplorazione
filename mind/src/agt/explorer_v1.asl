// ============================================================
// explorer.asl — Variante 1: agente intelligente
// Il percorso viene calcolato nella mente dell'agente
// tramite l'internal action vesna.ComputePath (A*)
//
// + Specchio del grafo verso PathFinderArtifact (additivo,
//   non sostituisce la logica di navigazione esistente)
// ============================================================

+!start <-
    .print("[explorer] in attesa del grafo...");
    makeArtifact("pathfinder", "artifact.PathFinderArtifact", [], PathHandle);
    focus(PathHandle);
    +pathfinder(PathHandle).

+node(Id, Type, Floor, X, Y, Z, CorridorId, PoleLabel, WsPort) <-
    .print("[explorer] nodo: ", Id);
    ?pathfinder(H);
    addNode(Id, Type, Floor, X, Y, Z)[artifact_id(H)].

+edge(GoName, From, To, Floor, EdgeType, DoorState, Side, Dir, DistA, DistB, WsPort) <-
    .print("[explorer] arco: ", GoName, " da ", From, " a ", To);
    ?pathfinder(H);
    addEdge(From, To, EdgeType, Dir, DistA, DistB)[artifact_id(H)].

+connector_link(Id, FloorA, FloorB, Bidirectional, PosAx, PosAy, PosAz, PosBx, PosBy, PosBz) <-
    .print("[explorer] connettore: ", Id, " piano ", FloorA, " <-> ", FloorB);
    ?pathfinder(H);
    addNode(Id, "Connector", FloorA, PosAx, PosAy, PosAz)[artifact_id(H)].

// ── NUOVO: door_dist non aveva un piano dedicato, ora viene anche
//           inoltrato al PathFinderArtifact per la risoluzione
//           dei pesi sugli archi "none"/"seg"
+door_dist(RoomA, RoomB, Floor, Dist) <-
    ?pathfinder(H);
    addDoorDist(RoomA, RoomB, Dist)[artifact_id(H)].

+corridor(CorridorId, PoleA, PoleB, Floor, WsPort) <-
    .print("[explorer] corridoio: ", CorridorId);
    ?node(PoleA, _, _, X1, Y1, Z1, _, _, _);
    ?node(PoleB, _, _, X2, Y2, Z2, _, _, _);
    makeArtifact(CorridorId, "artifact.CorridorArtifact",
                 [CorridorId, WsPort, X1, Y1, Z1, X2, Y2, Z2], _).

+new_object(ArtifactId, RoomId, ArtifactType, WsPort, X, Y, Z) <-
    .print("[explorer] oggetto: ", ArtifactId);
    makeArtifact(ArtifactId, "artifact.RoomObjectArtifact",
                 [ArtifactId, WsPort, RoomId, X, Y, Z], Handle);
    focus(Handle);
    ?pathfinder(H);
    addNewObject(ArtifactId, RoomId)[artifact_id(H)].

+exploration_complete(BuildingId, _, _, TotalNodes, TotalEdges, _) <-
    .print("[explorer] grafo completo: ", TotalNodes, " nodi, ", TotalEdges, " archi");
    !create_door_artifacts;
    ?pathfinder(H);
    graphReady[artifact_id(H)].
    // Nota: navigate_to viene chiamato quando arriva current_room    // Nota: navigate_to viene chiamato quando arriva current_room

// Quando riceviamo la stanza corrente → calcola il percorso
+current_room(StartId) <-
    .print("[explorer] Sono in: ", StartId);
    !navigate_to(StartId, "Laboratorio3").  // ← test, cambia con goal dinamico


// ── Crea artefatti porte/varchi ───────────────────────────────────────────
+!create_door_artifacts <-
    for( edge(GoName, From, To, Floor, EdgeType, DoorState, Side, Dir, DistA, DistB, WsPort) &
         Dir == "fw" ) {
        if (DoorState == "Open") {
            makeArtifact(GoName, "artifact.VarcoArtifact",
                         [GoName, WsPort, 0.0, 0.0, 0.0], _)
        } else {
            makeArtifact(GoName, "artifact.DoorArtifact",
                         [GoName, WsPort, "false", "false", 0.0, 0.0, 0.0], _)
        }
    }.

// ── Variante 1: naviga verso una stanza usando A* nella mente ────────────
// L'internal action vesna.ComputePath legge le beliefs e calcola il percorso
+!navigate_to(StartId, GoalId) <-
    .print("[explorer] Calcolo percorso: ", StartId, " → ", GoalId);
    vesna.ComputePath(StartId, GoalId, Path, Cost);
    if (Path == []) {
        .print("[explorer] Nessun percorso trovato da ", StartId, " a ", GoalId)
    } else {
        .print("[explorer] Percorso (", Cost, "m): ", Path);
        !follow_path(Path)
    }.

// ── Naviga verso un artefatto ─────────────────────────────────────────────
+!navigate_to_artifact(StartId, ArtifactId) <-
    ?new_object(ArtifactId, RoomId, _, _, _, _, _);
    .print("[explorer] Artefatto '", ArtifactId, "' in stanza '", RoomId, "'");
    !navigate_to(StartId, RoomId).

// ── Segui il percorso nodo per nodo ──────────────────────────────────────
+!follow_path([]) <-
    .print("[explorer] Percorso completato.").

+!follow_path([NextNode | Rest]) <-
    .print("[explorer] Prossimo nodo: ", NextNode);
    // Qui andranno i comandi a Unity per muovere l'agente fisicamente
    !follow_path(Rest).
