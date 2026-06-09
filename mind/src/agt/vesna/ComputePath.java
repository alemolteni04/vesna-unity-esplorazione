package vesna;

import jason.asSemantics.*;
import jason.asSyntax.*;
import jason.bb.BeliefBase;
import pathfinding.AStarPathfinder;

import java.util.Iterator;
import java.util.List;

/**
 * ComputePath.java
 *
 * Internal action Jason per il calcolo del cammino minimo (A*).
 * Legge le beliefs node/edge/door_dist dal belief base dell'agente,
 * costruisce il grafo topologico e calcola il percorso ottimo.
 *
 * USO IN JASON:
 *   vesna.ComputePath(StartId, GoalId, Path, Cost)
 *
 * PARAMETRI:
 *   StartId  — [IN]  nodeId del nodo di partenza  (es. "Ufficio1")
 *   GoalId   — [IN]  nodeId del nodo destinazione (es. "Laboratorio3")
 *   Path     — [OUT] lista Jason dei nodeId del percorso ([N1,N2,...])
 *   Cost     — [OUT] costo totale del percorso in metri
 *
 * ESEMPIO:
 *   +!navigate_to(Goal) <-
 *       ?current_room(Start);
 *       vesna.ComputePath(Start, Goal, Path, Cost);
 *       .print("Percorso (", Cost, "m): ", Path);
 *       !follow_path(Path).
 *
 * ERRORE:
 *   Se non esiste percorso, Path = [] e Cost = 0.
 *   L'azione NON fallisce — gestisci il caso Path=[] nel piano.
 */
public class ComputePath extends DefaultInternalAction {

    @Override
    public int getMinArgs() { return 4; }

    @Override
    public int getMaxArgs() { return 4; }

    @Override
    public Object execute(TransitionSystem ts, Unifier un, Term[] args) throws Exception {

        // ── Leggi i parametri di input ────────────────────────────────────
        String startId = ((StringTerm) args[0]).getString();
        String goalId  = ((StringTerm) args[1]).getString();

        // ── Costruisci il grafo dalle beliefs ─────────────────────────────
        AStarPathfinder pathfinder = new AStarPathfinder();
        BeliefBase bb = ts.getAg().getBB();

        // ── Aggiungi nodi ─────────────────────────────────────────────────
        // Belief: node(Id, Type, Floor, X, Y, Z, CorridorId, PoleLabel, WsPort)
        Iterator<Literal> nodeIt = bb.iterator();

        if (nodeIt != null) {
            while (nodeIt.hasNext()) {
                Literal bel = nodeIt.next();
                if (bel.getFunctor().equals("node") && bel.getArity() == 9) {
                    try {
                        String id    = ((StringTerm) bel.getTerm(0)).getString();
                        String type  = ((StringTerm) bel.getTerm(1)).getString();
                        int    floor = (int) ((NumberTerm) bel.getTerm(2)).solve();
                        double x     = ((NumberTerm) bel.getTerm(3)).solve();
                        double y     = ((NumberTerm) bel.getTerm(4)).solve();
                        double z     = ((NumberTerm) bel.getTerm(5)).solve();
                        pathfinder.addNode(id, type, floor, x, y, z);
                    } catch (Exception e) {
                        // skip nodo malformato
                    }
                }
            }
        }
        System.out.println("[ComputePath] Nodi caricati: " + pathfinder.getNodeIds().size());


        // ── Aggiungi archi ────────────────────────────────────────────────
        // Belief: edge(GoName, From, To, Floor, EdgeType, DoorState,
        //              Side, Dir, DistA, DistB, WsPort)
        Iterator<Literal> edgeIt = bb.iterator();
        int edgeCount = 0;
        if (edgeIt != null) {
            while (edgeIt.hasNext()) {
                Literal bel = edgeIt.next();
                if (bel.getFunctor().equals("edge") && bel.getArity() == 11) {
                    try {
                        String from     = ((StringTerm) bel.getTerm(1)).getString();
                        String to       = ((StringTerm) bel.getTerm(2)).getString();
                        String edgeType = ((StringTerm) bel.getTerm(4)).getString();
                        String dir      = ((StringTerm) bel.getTerm(7)).getString();
                        double distA    = ((NumberTerm) bel.getTerm(8)).solve();
                        double distB    = ((NumberTerm) bel.getTerm(9)).solve();

                        double weight = 0.0;
                        switch (dir) {
                            case "fw"      -> weight = distA;
                            case "bw"      -> weight = distB;
                            case "seg"     -> weight = distA > 0 ? distA : distB;
                            case "central" -> { continue; }
                            case "none"    -> weight = getDoorDist(bb, from, to);
                        }

                        pathfinder.addEdge(from, to, weight, edgeType);
                        edgeCount++;  // ← aggiungi

                    } catch (Exception e) {
                        System.out.println("[ComputePath] Errore arco: " + e.getMessage()); // ← aggiungi
                    }
                }
            }
        }
        System.out.println("[ComputePath] Archi caricati: " + edgeCount); // ← aggiungi
        System.out.println("[ComputePath] Vicini di Ufficio1: " + pathfinder.getAdjacency("Ufficio1"));
        System.out.println("[ComputePath] Vicini di Laboratorio3: " + pathfinder.getAdjacency("Laboratorio3"));

        // ── Calcola il percorso con A* ─────────────────────────────────────
        List<String> path = pathfinder.computePath(startId, goalId);
        double cost = pathfinder.pathCost(path);

        pathfinder.printPath(path);

        // ── Costruisci la lista Jason [N1, N2, ...] ───────────────────────
        ListTerm pathList = ASSyntax.createList();
        if (!path.isEmpty()) {
            // Costruisci la lista in ordine inverso (addFirst)
            for (int i = path.size() - 1; i >= 0; i--) {
                pathList = ASSyntax.createList(
                    ASSyntax.createString(path.get(i)), pathList);
            }
        }

        // ── Unifica i risultati con i parametri di output ─────────────────
        return un.unifies(args[2], pathList) &&
               un.unifies(args[3], ASSyntax.createNumber(cost));
    }


    // -----------------------------------------------------------------------
    // Helper — cerca door_dist(From, To, _, Dist) nel belief base
    // -----------------------------------------------------------------------
    private double getDoorDist(BeliefBase bb, String from, String to) {
        // Prova door_dist(From, To, _, Dist)
       Iterator<Literal> it = bb.iterator();
        if (it == null) return 0.0;

        while (it.hasNext()) {
            Literal bel = it.next();
            if (bel.getFunctor().equals("door_dist") && bel.getArity() == 4) {
                try {
                    String roomA = ((StringTerm) bel.getTerm(0)).getString();
                    String roomB = ((StringTerm) bel.getTerm(1)).getString();

                    if ((roomA.equals(from) && roomB.equals(to)) ||
                        (roomA.equals(to)   && roomB.equals(from))) {
                        return ((NumberTerm) bel.getTerm(3)).solve();
                    }
                } catch (Exception e) {
                    // skip
                }
            }
        }
        return 0.0;
    }
}
