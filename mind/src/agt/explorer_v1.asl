// ============================================================
// explorer.asl — Variante 1
// ============================================================

// Piani di movimento della libreria (reach_dest -> walk(goto,Next) a Unity)
{ include("libraryPlans.asl") }

// L'explorer non vaga: sopprime l'eventuale start_walking della libreria
+!start_walking <- true.

!start.

+!start <-
    !ensure_env_manager;
    .print("[explorer] pronto, ricevo il grafo da Unity...").

// ── Aggancio manuale all'envManager: lookup + focus esplicito ────────────
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
    !create_door_artifacts;
    !poll_target.


+current_room(_) : at(_) <- true.            // già nota → ignora (anti-stale)
+current_room(Where) <-
    +at(Where);
    .print("[explorer] posizione iniziale: ", Where).


// ── Polling del target dal bridge (ai_bridge.py → target.json) ────────────
+!poll_target <-
    .wait(1500);
    !try_navigate;
    !poll_target.

+!try_navigate : at(Start) & not navigating <-
    vesna.CheckTarget(Goal, Artifact);
    +navigating;
    .print("[explorer] target: ", Goal);
    !navigate_to(Start, Goal);
    !final_approach(Goal, Artifact);
    -navigating.

// rete di sicurezza: guardia falsa (non in at/_ o già navigating) → salta il giro
+!try_navigate <- true.

// CheckTarget ritorna false quando non c'è un nuovo target: normale, pulisci e continua
-!try_navigate <- .abolish(navigating).

// target-stanza puro (ai_bridge.py): Artifact = "" → ci si ferma alla stanza,
// at(_) lo aggiorna già Unity con current_room.
+!final_approach(_, "", _, _, _) <- true.
+!final_approach(_, "") <- true.

+!final_approach(Room, Artifact) <-
    !!reach_dest(Artifact);
    -movement_in_progress(Artifact);
    .abolish(at(_));
    +at(Room);
    .print("[explorer] arrivato a ", Artifact, ", stanza corrente: ", Room).


// ── Crea artefatti porte/varchi ───────────────────────────────────────────
+!create_door_artifacts <-
    for( edge(GoName, From, To, Floor, EdgeType, DoorState, Side, Dir, DistA, DistB, WsPort) ) {

        // fw = faccia frontale porta, none = porta tra stanze, seg = varco
        // Ignoriamo i "bw" per non creare cloni doppi della stessa porta
        if (Dir == "fw" | Dir == "none" | Dir == "seg") {

            if (DoorState == "Open") {
                makeArtifact(GoName, "artifact.VarcoArtifact",
                             [GoName, WsPort, 0.0, 0.0, 0.0], _)
            } else {
                makeArtifact(GoName, "artifact.DoorArtifact",
                             [GoName, WsPort, "false", "false", 0.0, 0.0, 0.0], _)
            }
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
        Path = [_First | StepsToWalk];   // scarta il nodo di partenza (ci sono già)
        !follow_path(StepsToWalk);
        .print("[explorer] Percorso completato.")
    }.


// nodo intermedio: c'è ancora del percorso dopo
+!follow_path([Next | Rest]) : Rest \== [] <-
    .print("[explorer] -> ", Next);
    !!reach_dest(Next);
    .wait({ +reached(place, Next) });
    -movement_in_progress(Next);
    .abolish(at(_)); +at(Next);
    .print("[explorer] arrivato in: ", Next);
    !follow_path(Rest).

// ultimo nodo: dopo non c'è più nulla → è la destinazione
+!follow_path([Last]) <-
    .print("[explorer] -> ", Last);
    !!reach_dest(Last);
    .wait({ +reached(place, Last) });
    -movement_in_progress(Last);
    .abolish(at(_)); +at(Last);
    .print("[explorer] sei a destinazione: ", Last).

+!follow_path([]).