// ============================================================
// explorer.asl — Variante 1
// ============================================================
!start.

+!start <-
    !ensure_env_manager;
    .print("[explorer] pronto, ricevo il grafo da Unity...").

// ── Aggancio manuale all'envManager: lookup + focus esplicito ────────────
// Senza il focus nel JCM, lo facciamo qui direttamente.
+!ensure_env_manager <-
	joinWorkspace("/main/main", _);
    lookupArtifact("envManager", EnvId);
    focus(EnvId);
    +env_manager(EnvId);
    .print("[explorer] envManager agganciato: ", EnvId).


	
-!ensure_env_manager <-
    .print("[explorer] ensure_env_manager fallito, ritento...");
    .wait(500);
    !ensure_env_manager.

// ── Helper: attende env_manager se non ancora disponibile ────────────────
+!get_env(E) : env_manager(E).
+!get_env(E) <-
    .wait({+env_manager(_)}, 10000, _);
    ?env_manager(E).

// ── Ricezione grafo da Unity ──────────────────────────────────────────────
+node(Id, Type, Floor, X, Y, Z, CorridorId, PoleLabel, WsPort) <-
    .print("[explorer] nodo: ", Id);
    !get_env(E);
    addNode(Id, Type, Floor, X, Y, Z)[artifact_id(E)].

+edge(GoName, From, To, Floor, EdgeType, DoorState, Side, Dir, DistA, DistB, WsPort) <-
    .print("[explorer] arco: ", GoName, " da ", From, " a ", To);
    !get_env(E);
    addEdge(From, To, EdgeType, Dir, DistA, DistB)[artifact_id(E)].

+connector_link(Id, FloorA, FloorB, Bidirectional, PosAx, PosAy, PosAz, PosBx, PosBy, PosBz) <-
    .print("[explorer] connettore: ", Id, " piano ", FloorA, " <-> ", FloorB);
    !get_env(E);
    addNode(Id, "Connector", FloorA, PosAx, PosAy, PosAz)[artifact_id(E)].

+door_dist(RoomA, RoomB, Floor, Dist) <-
    !get_env(E);
    addDoorDist(RoomA, RoomB, Dist)[artifact_id(E)].

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
    !get_env(E);
    addNewObject(ArtifactId, RoomId)[artifact_id(E)].

+exploration_complete(BuildingId, _, _, TotalNodes, TotalEdges, _) <-
    .print("[explorer] grafo completo: ", TotalNodes, " nodi, ", TotalEdges, " archi");
    !get_env(E);
    graphReady[artifact_id(E)];
    !create_door_artifacts.

// ── Due piani per current_room ────────────────────────────────────────────
+current_room(StartId) : exploration_complete(_, _, _, _, _, _) <-
    .print("[explorer] Sono in: ", StartId, " — grafo pronto, navigo");
    !navigate_to(StartId, "Laboratorio3").

+current_room(StartId) <-
    .print("[explorer] Sono in: ", StartId, " — grafo non ancora pronto").

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

// ── Naviga verso una stanza usando A* nella mente ────────────────────────
+!navigate_to(StartId, GoalId) <-
    .print("[explorer] Calcolo percorso: ", StartId, " → ", GoalId);
    vesna.ComputePath(StartId, GoalId, Path, Cost);
    if (Path == []) {
        .print("[explorer] Nessun percorso trovato da ", StartId, " a ", GoalId)
    } else {
        .print("[explorer] Percorso (", Cost, "m): ", Path);
        !follow_path(Path)
    }.

+!navigate_to_artifact(StartId, ArtifactId) <-
    ?new_object(ArtifactId, RoomId, _, _, _, _, _);
    .print("[explorer] Artefatto '", ArtifactId, "' in stanza '", RoomId, "'");
    !navigate_to(StartId, RoomId).

// ── Segui il percorso nodo per nodo ──────────────────────────────────────
+!follow_path([]) <-
    .print("[explorer] Percorso completato.").

+!follow_path([NextNode | Rest]) <-
    .print("[explorer] Prossimo nodo: ", NextNode);
    !follow_path(Rest).
