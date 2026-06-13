package artifact;

import cartago.Artifact;
import cartago.OPERATION;
import jason.asSyntax.*;
import pathfinding.AStarPathfinder;

import java.util.ArrayList;
import java.util.List;

/**
 * PathFinderArtifact.java
 *
 * Artifact CArtAgO "specchio" del grafo topologico.
 * Riceve gli stessi dati (node/edge/door_dist) che l'agente esploratore
 * mette nel proprio belief base, e li accumula in forma grezza.
 *
 * Al momento di computePath() ricostruisce un AStarPathfinder con la
 * STESSA logica di risoluzione peso usata in ComputePath.java
 * (fw -> distA, bw -> distB, seg -> distA/distB, central -> skip,
 *  none -> door_dist), poi esegue A* e pubblica il risultato come
 * observable property "path(Start, Goal, Path, Cost)".
 *
 * USO LATO AGENTSPEAK (additivo, non sostituisce ComputePath):
 *
 *   +node(Id,Type,Floor,X,Y,Z,_,_,_) <-
 *       ... (logica esistente) ...;
 *       addNode(Id,Type,Floor,X,Y,Z)[artifact_name("pathfinder")].
 *
 *   +edge(_,From,To,_,EdgeType,_,_,Dir,DistA,DistB,_) <-
 *       ... (logica esistente) ...;
 *       addEdge(From,To,EdgeType,Dir,DistA,DistB)[artifact_name("pathfinder")].
 *
 *   +door_dist(RoomA,RoomB,_,Dist) <-
 *       addDoorDist(RoomA,RoomB,Dist)[artifact_name("pathfinder")].
 *
 *   computePath(StartId,GoalId)[artifact_name("pathfinder")];
 *   ?path(_,_,Path,Cost)[artifact_name("pathfinder")].
 */
public class PathFinderArtifact extends Artifact {

    // Dati grezzi accumulati man mano che arrivano dall'esploratore
    private final List<Object[]> rawNodes    = new ArrayList<>(); // id,type,floor,x,y,z
    private final List<Object[]> rawEdges    = new ArrayList<>(); // from,to,edgeType,dir,distA,distB
    private final List<Object[]> rawDoorDist = new ArrayList<>(); // roomA,roomB,dist
    private final List<Object[]> rawObjects  = new ArrayList<>(); // artifactId,roomId

    private static final String STATE_FILE =
            System.getProperty("user.home")
            + "\\AppData\\LocalLow\\DefaultCompany\\JaCaMoIntegration\\graph_snapshots\\pathfinder_state.json";

    void init() {
        defineObsProperty("graphReady", false);
        // proprietà placeholder, viene aggiornata al primo computePath
        defineObsProperty("path", "", "", ASSyntax.createList(), 0.0);

        if (loadFromDisk()) {
            getObsProperty("graphReady").updateValue(true);
            signal("graphReady");
            log("[PathFinderArtifact] Grafo caricato da disco (" + rawNodes.size() + " nodi).");
        }
    }

    // -------------------------------------------------------------------
    // Popolamento del grafo (chiamato dall'esploratore in parallelo al BB)
    // -------------------------------------------------------------------

    @OPERATION
    public void addNode(String id, String type, int floor, double x, double y, double z) {
        rawNodes.add(new Object[]{id, type, floor, x, y, z});
    }

    @OPERATION
    public void addEdge(String from, String to, String edgeType, String dir,
                         double distA, double distB) {
        rawEdges.add(new Object[]{from, to, edgeType, dir, distA, distB});
    }

    @OPERATION
    public void addDoorDist(String roomA, String roomB, double dist) {
        rawDoorDist.add(new Object[]{roomA, roomB, dist});
    }

    @OPERATION
    public void addNewObject(String artifactId, String roomId) {
        rawObjects.add(new Object[]{artifactId, roomId});
    }

    @OPERATION
    public void graphReady() {
        saveToDisk();
        getObsProperty("graphReady").updateValue(true);
        signal("graphReady");
    }

    // -------------------------------------------------------------------
    // Calcolo del percorso
    // -------------------------------------------------------------------

    /**
     * Calcola il percorso da startId a goalId.
     * Se goalId non è un nodo del grafo, viene cercato tra gli oggetti
     * registrati (new_object) e risolto nella stanza che lo contiene.
     */
    @OPERATION
    public void computePath(String startId, String goalId) {
        AStarPathfinder pf = buildGraph();

        String resolvedGoal = goalId;
        if (!pf.getNodeIds().contains(goalId)) {
            for (Object[] o : rawObjects) {
                if (o[0].equals(goalId)) {
                    resolvedGoal = (String) o[1];
                    break;
                }
            }
        }

        List<String> path;
        double cost;

        if (pf.getNodeIds().contains(resolvedGoal)) {
            path = pf.computePath(startId, resolvedGoal);
            cost = pf.pathCost(path);
        } else {
            // Goal non è un nodo diretto: probabilmente un "corridoio"
            // espresso senza pole (es. "Corridoio2"). Provo i poli A/B
            // e scelgo il percorso più economico.
            String poleA = resolvedGoal + "_A";
            String poleB = resolvedGoal + "_B";

            List<String> pathA = pf.getNodeIds().contains(poleA)
                    ? pf.computePath(startId, poleA) : List.of();
            double costA = pathA.isEmpty() ? Double.MAX_VALUE : pf.pathCost(pathA);

            List<String> pathB = pf.getNodeIds().contains(poleB)
                    ? pf.computePath(startId, poleB) : List.of();
            double costB = pathB.isEmpty() ? Double.MAX_VALUE : pf.pathCost(pathB);

            if (costA == Double.MAX_VALUE && costB == Double.MAX_VALUE) {
                path = List.of();
                cost = 0.0;
            } else if (costA <= costB) {
                path = pathA; cost = costA; resolvedGoal = poleA;
            } else {
                path = pathB; cost = costB; resolvedGoal = poleB;
            }
        }

        pf.printPath(path);

       ListTerm pathList = ASSyntax.createList();
        for (String nodeId : path) {
            pathList.add(ASSyntax.createString(nodeId));
        }

        getObsProperty("path").updateValues(
            startId, resolvedGoal, pathList, cost
        );
    }

    // -------------------------------------------------------------------
    // Costruzione del grafo A* dai dati grezzi (logica = ComputePath.java)
    // -------------------------------------------------------------------

    private AStarPathfinder buildGraph() {
        AStarPathfinder pf = new AStarPathfinder();

        for (Object[] n : rawNodes) {
            pf.addNode((String) n[0], (String) n[1], (int) n[2],
                       (double) n[3], (double) n[4], (double) n[5]);
        }

        for (Object[] e : rawEdges) {
            String from     = (String) e[0];
            String to       = (String) e[1];
            String edgeType = (String) e[2];
            String dir      = (String) e[3];
            double distA    = (double) e[4];
            double distB    = (double) e[5];

            double weight;
            switch (dir) {
                case "fw"      -> weight = distA;
                case "bw"      -> weight = distB;
                case "seg"     -> weight = distA > 0 ? distA : distB;
                case "central" -> { continue; }
                case "none"    -> weight = getDoorDist(from, to);
                default        -> weight = 0.0;
            }

            pf.addEdge(from, to, weight, edgeType);
        }

        return pf;
    }

    private double getDoorDist(String from, String to) {
        for (Object[] d : rawDoorDist) {
            String roomA = (String) d[0];
            String roomB = (String) d[1];
            if ((roomA.equals(from) && roomB.equals(to)) ||
                (roomA.equals(to)   && roomB.equals(from))) {
                return (double) d[2];
            }
        }
        return 0.0;
    }

    // -------------------------------------------------------------------
    // Persistenza: salva/carica i dati grezzi su disco, così le run
    // successive non devono aspettare una nuova esplorazione.
    // -------------------------------------------------------------------
    private void saveToDisk() {
        try {
            org.json.JSONObject root = new org.json.JSONObject();

            org.json.JSONArray nodesArr = new org.json.JSONArray();
            for (Object[] n : rawNodes) {
                org.json.JSONArray a = new org.json.JSONArray();
                for (Object v : n) a.put(v);
                nodesArr.put(a);
            }

            org.json.JSONArray edgesArr = new org.json.JSONArray();
            for (Object[] e : rawEdges) {
                org.json.JSONArray a = new org.json.JSONArray();
                for (Object v : e) a.put(v);
                edgesArr.put(a);
            }

            org.json.JSONArray doorDistArr = new org.json.JSONArray();
            for (Object[] d : rawDoorDist) {
                org.json.JSONArray a = new org.json.JSONArray();
                for (Object v : d) a.put(v);
                doorDistArr.put(a);
            }

            org.json.JSONArray objectsArr = new org.json.JSONArray();
            for (Object[] o : rawObjects) {
                org.json.JSONArray a = new org.json.JSONArray();
                for (Object v : o) a.put(v);
                objectsArr.put(a);
            }

            root.put("nodes", nodesArr);
            root.put("edges", edgesArr);
            root.put("doorDist", doorDistArr);
            root.put("objects", objectsArr);

            java.io.File f = new java.io.File(STATE_FILE);
            f.getParentFile().mkdirs();
            java.nio.file.Files.write(f.toPath(), root.toString(2).getBytes("UTF-8"));
            log("[PathFinderArtifact] Stato salvato in " + STATE_FILE);
        } catch (Exception e) {
            log("[PathFinderArtifact] Errore salvataggio: " + e.getMessage());
        }
    }

    private boolean loadFromDisk() {
        try {
            java.io.File f = new java.io.File(STATE_FILE);
            if (!f.exists()) return false;

            String content = new String(java.nio.file.Files.readAllBytes(f.toPath()), "UTF-8");
            org.json.JSONObject root = new org.json.JSONObject(content);

            org.json.JSONArray nodesArr = root.getJSONArray("nodes");
            for (int i = 0; i < nodesArr.length(); i++) {
                org.json.JSONArray a = nodesArr.getJSONArray(i);
                rawNodes.add(new Object[]{
                    a.getString(0), a.getString(1), a.getInt(2),
                    a.getDouble(3), a.getDouble(4), a.getDouble(5)
                });
            }

            org.json.JSONArray edgesArr = root.getJSONArray("edges");
            for (int i = 0; i < edgesArr.length(); i++) {
                org.json.JSONArray a = edgesArr.getJSONArray(i);
                rawEdges.add(new Object[]{
                    a.getString(0), a.getString(1), a.getString(2),
                    a.getString(3), a.getDouble(4), a.getDouble(5)
                });
            }

            org.json.JSONArray doorDistArr = root.getJSONArray("doorDist");
            for (int i = 0; i < doorDistArr.length(); i++) {
                org.json.JSONArray a = doorDistArr.getJSONArray(i);
                rawDoorDist.add(new Object[]{
                    a.getString(0), a.getString(1), a.getDouble(2)
                });
            }

            org.json.JSONArray objectsArr = root.getJSONArray("objects");
            for (int i = 0; i < objectsArr.length(); i++) {
                org.json.JSONArray a = objectsArr.getJSONArray(i);
                rawObjects.add(new Object[]{ a.getString(0), a.getString(1) });
            }

            return !rawNodes.isEmpty();
        } catch (Exception e) {
            log("[PathFinderArtifact] Errore caricamento: " + e.getMessage());
            return false;
        }
    }
}