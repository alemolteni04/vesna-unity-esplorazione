// vesna.asl
//
// Agente navigante. Riceve comandi target dall'AI bridge (target.json,
// letto tramite l'internal action checkTarget), calcola il percorso
// minimo con A* (computePath) partendo dalla stanza corrente
// (belief current_room, aggiornata da LocalizationTest.cs) e lo segue
// nodo per nodo, aspettando la confermata di arrivo da Unity prima di
// proseguire al nodo successivo.

// ── Avvio: localizzazione iniziale + loop di controllo comandi ───────────
!start.

+!start <-
    .print("[vesna] avvio, attendo localizzazione iniziale...");
    lookupArtifact("pathfinder", PfId);
    focus(PfId);
    +pathfinder(PfId);
    .print("[vesna] attendo che il grafo sia pronto (graphReady)...");
    .wait(graphReady(true), 600000, _);
    .print("[vesna] grafo pronto.");
    .wait(current_room(_), 60000, _);
    ?current_room(Start);
    .print("[vesna] posizione iniziale: ", Start);
    !check_target_loop.


// ── Loop: controlla periodicamente se è arrivato un nuovo target ─────────
+!check_target_loop <-
    .wait(2000);
    if (vesna.CheckTarget(Target)) {
        .print("[vesna] nuovo comando ricevuto: ", Target);
        !go_to(Target);
    };
    !check_target_loop.


// ── Goal principale: vai verso Target (stanza o corridoio) ────────────────
+!go_to(Target) : current_room(Start) & pathfinder(PfId) <-
    .print("[vesna] da ", Start, " verso ", Target);
    computePath(Start, Target)[artifact_id(PfId)];
    .wait({+path(Start, _, _, _)}, 5000, _);
    ?path(Start, ResolvedGoal, Path, Cost);
    if (Path == []) {
        .print("[vesna] ERRORE: nessun percorso trovato verso ", Target);
    } else {
        .print("[vesna] percorso verso ", ResolvedGoal, " (", Cost, "m): ", Path);
        Path = [_First|StepsToWalk];
        !follow_path(StepsToWalk);
        .print("[vesna] arrivata a destinazione: ", Target);
    }.

// Se per qualche motivo current_room non è ancora disponibile
+!go_to(Target) <-
    .print("[vesna] posizione attuale non nota, ignoro comando: ", Target).


// ── Segui il percorso nodo per nodo ───────────────────────────────────────
+!follow_path([]).

+!follow_path([Next | Rest]) <-
    .print("[vesna] -> ", Next);
    vesna.walk(Next, _);
    .wait({+movement(completed, destination_reached)}, 30000, _);
    !follow_path(Rest).


// ── Log di debug sugli aggiornamenti di posizione ─────────────────────────
+current_room(Room) <-
    .print("[vesna] ora sono in: ", Room).