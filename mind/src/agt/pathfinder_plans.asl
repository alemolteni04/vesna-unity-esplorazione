// ============================================================
// pathfinder_plans.asl
// ============================================================
// Piani Jason per creare e usare PathFinderArtifact.
// Da aggiungere in explorer.asl dopo exploration_complete.
// ============================================================

// ── 1. Quando l'esplorazione è completa → crea il PathFinder ─────────────
+exploration_complete(BuildingId, _, _, TotalNodes, TotalEdges, _) <-
    .print("[explorer] grafo completo: ", TotalNodes, " nodi, ", TotalEdges, " archi");
    !create_door_artifacts;
    !build_pathfinder.

// ── 2. Costruisce il PathFinderArtifact dal grafo nelle beliefs ───────────
+!build_pathfinder <-
    .print("[explorer] Costruzione PathFinderArtifact...");

    // Crea l'artefatto
    makeArtifact("pathfinder", "artifacts.PathFinderArtifact", [], PathHandle);
    focus(PathHandle);

    // Popola con tutti i nodi
    for ( node(Id, Type, Floor, X, Y, Z, _, _, _) ) {
        addNode(Id, Type, Floor, X, Y, Z) [artifact_name("pathfinder")]
    };

    // Popola con tutti gli archi che hanno peso > 0
    for ( edge(GoName, From, To, Floor, EdgeType, _, _, Direction, DistA, DistB, _) &
          Direction \== "central" ) {
        if ( Direction == "fw" | Direction == "seg" ) {
            addEdge(From, To, DistA, EdgeType) [artifact_name("pathfinder")]
        } else {
            addEdge(From, To, DistB, EdgeType) [artifact_name("pathfinder")]
        }
    };

    // Segnala che il grafo è pronto
    graphReady [artifact_name("pathfinder")];
    .print("[explorer] PathFinderArtifact pronto.").

// ── 3. Piano per navigare verso una stanza ────────────────────────────────
+!navigate_to(GoalRoomId) <-
    .print("[explorer] Navigazione verso: ", GoalRoomId);

    // Recupera la stanza corrente dell'agente
    ?current_room(StartId);

    // Chiede il percorso all'artefatto
    computePath(StartId, GoalRoomId) [artifact_name("pathfinder")];

    // Legge il risultato dall'observable property
    ?path(_, _, Path, Cost);

    .print("[explorer] Percorso (", Cost, "m): ", Path);
    !follow_path(Path).

// ── 4. Piano per navigare verso un artefatto ─────────────────────────────
+!navigate_to_artifact(ArtifactId) <-
    // Trova la stanza dell'artefatto dalle beliefs
    ?new_object(ArtifactId, RoomId, _, _, _, _, _);
    .print("[explorer] Artefatto '", ArtifactId, "' in stanza '", RoomId, "'");
    !navigate_to(RoomId).

// ── 5. Piano per seguire il percorso calcolato ───────────────────────────
+!follow_path([]) <-
    .print("[explorer] Percorso completato.").

+!follow_path([NextNode | Rest]) <-
    .print("[explorer] Prossimo nodo: ", NextNode);
    // Qui andranno i comandi a Unity per muovere l'agente
    // Per ora stampa solo il percorso
    !follow_path(Rest).

// ── 6. Reazione quando il percorso viene aggiornato ──────────────────────
+path(Start, Goal, Path, Cost) <-
    .print("[explorer] Nuovo percorso calcolato: ", Start, " → ", Goal,
           " (", Cost, "m) nodi: ", Path).
