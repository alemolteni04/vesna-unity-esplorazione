// vesna.asl
// Sovrascrive il piano della libreria che fa vesna.walk(random) subito
+!start_walking <- true.

{ include("libraryPlans.asl") }
{ include("initialGoalsPlan.asl") }

// ── Avvio: aggancio envManager + loop comandi ─────────────────────────────
!start.
+!start <-
    .wait(2000);
    .print("[vesna] avviata, aggancio envManager...");
    !ensure_env_manager;
    .print("[vesna] attendo grafo pronto...");
    !wait_graph_ready;
    .print("[vesna] attendo posizione iniziale...");
    .wait({+current_room(_)}, 60000, _);
    ?current_room(Start);
    .print("[vesna] posizione iniziale: ", Start);
    !check_target_loop.

+!wait_graph_ready : graphReady(true).
+!wait_graph_ready <-
    .wait({+graphReady(true)}, 120000, _).

// ── Aggancio manuale all'envManager (stesso pattern dell'explorer) ─────────
+!ensure_env_manager <-
	joinWorkspace("/main/main", _);
    lookupArtifact("envManager", EnvId);
    focus(EnvId);
    +env_manager(EnvId);
    .print("[vesna] envManager agganciato: ", EnvId).

-!ensure_env_manager <-
    .print("[vesna] ensure_env_manager fallito, ritento...");
    .wait(500);
    !ensure_env_manager.

+!check_target_loop <-
    .wait(2000);
    if (vesna.CheckTarget(Target, X, Y, Z, Artifact)) {
        .print("[vesna] nuovo comando ricevuto: ", Target, " (oggetto: ", Artifact, ")");
        !go_to(Target, X, Y, Z, Artifact);
    };
    !check_target_loop.

// ── Goal principale: vai verso Target ─────────────────────────────────────

// già in movimento: ignora il comando (Correzione 3 della prof)
+!go_to(Target, _, _, _, _) : movement_in_progress(ActualDest) <-
    .print("[vesna] Comando ignorato: movimento in corso verso ", ActualDest).

// comando valido: naviga alla stanza, poi (se artefatto) avvicinati all'oggetto
+!go_to(Target, X, Y, Z, Artifact) : current_room(Start) & env_manager(EnvId) <-
    .print("[vesna] da ", Start, " verso ", Target);
    computePath(Start, Target, ResolvedGoal, Path, Cost)[artifact_id(EnvId)];
    if (Path == []) {
        .print("[vesna] ERRORE: nessun percorso verso ", Target);
    } else {
        .print("[vesna] percorso verso ", ResolvedGoal, " (", Cost, "m): ", Path);
        Path = [_First|StepsToWalk];
        !follow_path(StepsToWalk);
        .print("[vesna] arrivata alla stanza: ", Target);
        !final_approach(Target, Artifact, X, Y, Z);
    }.

+!go_to(Target, _, _, _, _) <-
    .print("[vesna] posizione non nota o envManager non pronto, ignoro: ", Target).

// target-stanza puro: nessun oggetto, ci si ferma alla stanza
+!final_approach(_, "", _, _, _) <- true.

// target-artefatto: gamba finale verso l'oggetto, poi registra la sua stanza
+!final_approach(Room, Artifact, X, Y, Z) <-
    .print("[vesna] avvicinamento all'oggetto ", Artifact, " @ (", X, ",", Y, ",", Z, ")");
    +movement_in_progress(Artifact);
    vesna.walk(X, Y, Z, Artifact);
    .wait({ +reached(place, Artifact) });
    -movement_in_progress(Artifact);
    -+current_room(Room);
    .print("[vesna] arrivata all'oggetto ", Artifact, ", stanza corrente: ", Room).

    
// ── Segui il percorso nodo per nodo ───────────────────────────────────────
+!follow_path([]).

+!follow_path([Next | Rest]) <-
    .print("[vesna] -> ", Next);
    
    // 1. Invia il comando a Unity tramite la libreria della prof
    !!reach_dest(Next); 
    
    // 2. CORREZIONE: Aspetta il formato esatto generato dal framework (+reached)
    .wait({ +reached(place, Next) });
    
    // 3. Rimuove la guardia e aggiorna la posizione per il prossimo nodo
    -movement_in_progress(Next);
    -+current_room(Next);
    
    !follow_path(Rest).

// ── Log di debug sugli aggiornamenti di posizione ──
+current_room(Room) : not last_printed_room(Room) <-
    -+last_printed_room(Room);
    .print("[vesna] AGGIORNAMENTO: ora sono in: ", Room).

+current_room(_). // Ignora i doppioni

// Sovrascrive il piano della libreria che fa vesna.walk(random) subito
+!start_walking <- true.