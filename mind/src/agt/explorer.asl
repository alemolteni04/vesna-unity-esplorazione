+!start <- .print("[explorer] in attesa del grafo...").

+node(Id, Type, Floor, X, Y, Z, CorridorId, PoleLabel, WsPort) <-
    .print("[explorer] nodo: ", Id).

+edge(GoName, From, To, Floor, EdgeType, DoorState, Side, Dir, DistA, DistB, WsPort) <-
    .print("[explorer] arco: ", GoName, " da ", From, " a ", To).

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
    focus(Handle).

+exploration_complete(BuildingId, _, _, TotalNodes, TotalEdges, _) <-
    .print("[explorer] grafo completo: ", TotalNodes, " nodi, ", TotalEdges, " archi");
    !create_door_artifacts;
    !build_pathfinder.       // ← aggiunto: costruisce il PathFinderArtifact

// ── Crea gli artefatti porte/varchi ──────────────────────────────────────
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

// ── Costruisce PathFinderArtifact dal grafo nelle beliefs ────────────────
+!build_pathfinder <-
    .print("[explorer] Costruzione PathFinderArtifact...");

    // Crea l'artefatto
    makeArtifact("pathfinder", "artifacts.PathFinderArtifact", [], PathHandle);
    focus(PathHandle);

    // Popola con tutti i nodi
    for ( node(Id, Type, Floor, X, Y, Z, _, _, _) ) {
        addNode(Id, Type, Floor, X, Y, Z) [artifact_id(PathHandle)]
    };

    for ( edge(_, From, To, _, EdgeType, _, _, Dir, DistA, DistB, _) &
      Dir \== "central" ) {
    if ( Dir == "fw" & DistA > 0 ) {
        addEdge(From, To, DistA, EdgeType) [artifact_id(PathHandle)]
    } elif ( Dir == "bw" & DistB > 0 ) {
        addEdge(From, To, DistB, EdgeType) [artifact_id(PathHandle)]
    } elif ( Dir == "none" ) {
        // usa door_dist per porte dirette stanza→stanza
        ?door_dist(From, To, _, Dist);
        addEdge(From, To, Dist, EdgeType) [artifact_id(PathHandle)]
    } elif ( Dir == "seg" ) {
        // usa door_dist per varchi corridoio→corridoio
        ?door_dist(From, To, _, Dist);
        addEdge(From, To, Dist, EdgeType) [artifact_id(PathHandle)]
    }
}.

    graphReady [artifact_id(PathHandle)];
    .print("[explorer] PathFinderArtifact pronto.");

    // ── TEST: calcola un percorso di prova ───────────────────────────────
    // Cambia le stanze con quelle presenti nel tuo edificio
    computePath("Ufficio1", "Laboratorio3") [artifact_id(PathHandle)].

// ── Naviga verso una stanza ──────────────────────────────────────────────
+!navigate_to(GoalRoomId) <-
    .print("[explorer] Navigazione verso: ", GoalRoomId);
    ?current_room(StartId);
    computePath(StartId, GoalRoomId) [artifact_id(PathHandle)];
    ?path(_, _, Path, Cost);
    .print("[explorer] Percorso (", Cost, "m): ", Path);
    !follow_path(Path).

// ── Naviga verso un artefatto ────────────────────────────────────────────
+!navigate_to_artifact(ArtifactId) <-
    ?new_object(ArtifactId, RoomId, _, _, _, _, _);
    .print("[explorer] Artefatto '", ArtifactId, "' in stanza '", RoomId, "'");
    !navigate_to(RoomId).

// ── Segui il percorso nodo per nodo ─────────────────────────────────────
+!follow_path([]) <-
    .print("[explorer] Percorso completato.").

+!follow_path([NextNode | Rest]) <-
    .print("[explorer] Prossimo nodo: ", NextNode);
    !follow_path(Rest).

// ── Reazione quando path viene aggiornato ────────────────────────────────
+path(Start, Goal, Path, Cost) <-
    .print("[explorer] Percorso: ", Start, " → ", Goal,
           " (", Cost, "m) nodi: ", Path).
