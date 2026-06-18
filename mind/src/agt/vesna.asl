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

// ── Loop: controlla periodicamente se è arrivato un nuovo target ──────────
+!check_target_loop <-
    .wait(2000);
    if (vesna.CheckTarget(Target)) {
        .print("[vesna] nuovo comando ricevuto: ", Target);
        !go_to(Target);
    };
    !check_target_loop.

// ── Goal principale: vai verso Target ─────────────────────────────────────

// AGGIUNTA: Guardia per rispettare la Correzione 3 della prof.
// Se l'agente si sta già muovendo, rifiuta o mette in attesa il nuovo comando.
+!go_to(Target) : movement_in_progress(ActualDest) <-
    .print("[vesna] Comando ignorato: movimento in corso verso ", ActualDest).

+!go_to(Target) : current_room(Start) & env_manager(EnvId) <-
    .print("[vesna] da ", Start, " verso ", Target);
    computePath(Start, Target, ResolvedGoal, Path, Cost)[artifact_id(EnvId)];
    if (Path == []) {
        .print("[vesna] ERRORE: nessun percorso verso ", Target);
    } else {
        .print("[vesna] percorso verso ", ResolvedGoal, " (", Cost, "m): ", Path);
        Path = [_First|StepsToWalk];
        !follow_path(StepsToWalk);
        .print("[vesna] arrivata: ", Target);
    }.

+!go_to(Target) <-
    .print("[vesna] posizione non nota o envManager non pronto, ignoro: ", Target).

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