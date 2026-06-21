package artifact;
import artifact.lib.maselements.AbstractMasElementArtifact;
import artifact.lib.model.WsMessage;
import artifact.lib.utils.ObjectMapperUtils;
import cartago.OPERATION;
import cartago.OpFeedbackParam;
import com.fasterxml.jackson.core.type.TypeReference;
import jason.asSyntax.ASSyntax;
import jason.asSyntax.ListTerm;
import org.json.JSONArray;
import org.json.JSONObject;
import pathfinding.AStarPathfinder;

import java.util.ArrayList;
import java.util.List;

public class EnvManagerArtifact extends AbstractMasElementArtifact {

    private final List<Object[]> rawNodes    = new ArrayList<>();
    private final List<Object[]> rawEdges    = new ArrayList<>();
    private final List<Object[]> rawDoorDist = new ArrayList<>();
    private final List<Object[]> rawObjects  = new ArrayList<>();

    private static final String STATE_FILE =
            System.getProperty("user.home")
            + "\\AppData\\LocalLow\\DefaultCompany\\JaCaMoIntegration\\graph_snapshots\\pathfinder_state.json";

    // -------------------------------------------------------------------
    // NUOVO init senza WebSocket: l'envManager non ha bisogno di WS,
    // solo di caricare il grafo da disco se esiste.
    // Nel .jcm: artifact envManager: artifact.EnvManagerArtifact("envManager")
    // -------------------------------------------------------------------
  @OPERATION
protected void init(String artifactName) {
    defineObsProperty("graphReady", false);
    System.out.println("[envManager] inizializzato (no WebSocket).");
    if (loadFromDisk()) {
        getObsProperty("graphReady").updateValue(true);
        signal("graphReady");   // <-- AGGIUNTO: notifica vesna anche se il grafo viene da disco
        System.out.println("[envManager] Grafo caricato da disco (" + rawNodes.size() + " nodi).");
    }
}

    // -------------------------------------------------------------------
    // Operazioni env manager (retrieval artefatti da Unity)
    // -------------------------------------------------------------------
    @OPERATION
    public void retrieveArtifactsByType(String artifactType, String agentName){
        try{
            lock.lock();
            WsMessage wsMessage = prepareMessage("retrieveArtifacts", "all_artifact_by_type", agentName, artifactType);
            writeLog("Sending message: " + ObjectMapperUtils.convertIntoJsonString(wsMessage));
            send(ObjectMapperUtils.convertIntoJsonString(wsMessage));
        }finally {
            lock.unlock();
        }
    }

    @OPERATION
    public void retrieveAllArtifacts(String agentName){
        try{
            lock.lock();
            WsMessage wsMessage = prepareMessage("retrieveArtifacts", "all_artifact", agentName, null);
            send(ObjectMapperUtils.convertIntoJsonString(wsMessage));
        }finally {
            lock.unlock();
        }
    }

    @OPERATION
    public void retrieveNearestArtifactByType(String artifactType, String agentName){
        try{
            lock.lock();
            WsMessage wsMessage = prepareMessage("retrieveArtifacts", "nearest", agentName, artifactType);
            writeLog("Sending message: " + ObjectMapperUtils.convertIntoJsonString(wsMessage));
            send(ObjectMapperUtils.convertIntoJsonString(wsMessage));
        }finally {
            lock.unlock();
        }
    }

    private static WsMessage prepareMessage(String messageType, String messagePayload, String agentName, String param) {
        WsMessage wsMessage = new WsMessage();
        wsMessage.setMessageType(messageType);
        wsMessage.setMessagePayload(messagePayload);
        wsMessage.setAgentName(agentName);
        wsMessage.setParam(param);
        return wsMessage;
    }

    @Override
    public void onConnectionEstablished(){
        super.onConnectionEstablished();
        execInternalOp("signalAllAgents", "connection_env_leader_established", true);
    }

    @Override
    public void onMessageReceived(String message) {
        writeLog("Message received from Unity: " + message);
        try{
            lock.lock();
            JSONObject messageJson = new JSONObject(message);
            String type = messageJson.getString("type");
            String receiver = messageJson.getString("receiver");

            if (!type.equals("arts_info"))
                throw new Exception("Wrong message type: " + type + ". Expected 'arts_info'.");

            List<String> artifactNames = messageJson.getJSONObject("data")
                    .getJSONArray("names")
                    .toList()
                    .stream()
                    .map(Object::toString)
                    .toList();

            execInternalOp("signalAgent", receiver, "artifact_names", artifactNames.toArray());
        }catch (Exception e){
            e.printStackTrace();
        }finally {
            lock.unlock();
        }
    }

    // ===================================================================
    // PATHFINDING
    // ===================================================================

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

    @OPERATION
    public void computePath(String startId, String goalId,
                            OpFeedbackParam<String> resolvedGoalOut,
                            OpFeedbackParam<Object>  pathOut,
                            OpFeedbackParam<Double>  costOut) {
        AStarPathfinder pf = buildGraph();

        String resolvedGoal = goalId;
        if (!pf.getNodeIds().contains(goalId)) {
            for (Object[] o : rawObjects) {
                if (o[0].equals(goalId)) { resolvedGoal = (String) o[1]; break; }
            }
        }

        List<String> path;
        double cost;

        if (pf.getNodeIds().contains(resolvedGoal)) {
            path = pf.computePath(startId, resolvedGoal);
            cost = pf.pathCost(path);
        } else {
            String poleA = resolvedGoal + "_A";
            String poleB = resolvedGoal + "_B";

            List<String> pathA = pf.getNodeIds().contains(poleA)
                    ? pf.computePath(startId, poleA) : List.of();
            double costA = pathA.isEmpty() ? Double.MAX_VALUE : pf.pathCost(pathA);

            List<String> pathB = pf.getNodeIds().contains(poleB)
                    ? pf.computePath(startId, poleB) : List.of();
            double costB = pathB.isEmpty() ? Double.MAX_VALUE : pf.pathCost(pathB);

            if (costA == Double.MAX_VALUE && costB == Double.MAX_VALUE) {
                path = List.of(); cost = 0.0;
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

        resolvedGoalOut.set(resolvedGoal);
        pathOut.set(pathList);
        costOut.set(cost);
    }

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
                case "central" -> weight = distA;
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

    private void saveToDisk() {
        try {
            JSONObject root = new JSONObject();

            JSONArray nodesArr = new JSONArray();
            for (Object[] n : rawNodes) {
                JSONArray a = new JSONArray();
                for (Object v : n) a.put(v);
                nodesArr.put(a);
            }

            JSONArray edgesArr = new JSONArray();
            for (Object[] e : rawEdges) {
                JSONArray a = new JSONArray();
                for (Object v : e) a.put(v);
                edgesArr.put(a);
            }

            JSONArray doorDistArr = new JSONArray();
            for (Object[] d : rawDoorDist) {
                JSONArray a = new JSONArray();
                for (Object v : d) a.put(v);
                doorDistArr.put(a);
            }

            JSONArray objectsArr = new JSONArray();
            for (Object[] o : rawObjects) {
                JSONArray a = new JSONArray();
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
            System.out.println("[envManager] Stato salvato in " + STATE_FILE);
        } catch (Exception e) {
            System.out.println("[envManager] Errore salvataggio: " + e.getMessage());
        }
    }

    private boolean loadFromDisk() {
        try {
            java.io.File f = new java.io.File(STATE_FILE);
            if (!f.exists()) return false;

            String content = new String(java.nio.file.Files.readAllBytes(f.toPath()), "UTF-8");
            JSONObject root = new JSONObject(content);

            JSONArray nodesArr = root.getJSONArray("nodes");
            for (int i = 0; i < nodesArr.length(); i++) {
                JSONArray a = nodesArr.getJSONArray(i);
                rawNodes.add(new Object[]{
                    a.getString(0), a.getString(1), a.getInt(2),
                    a.getDouble(3), a.getDouble(4), a.getDouble(5)
                });
            }

            JSONArray edgesArr = root.getJSONArray("edges");
            for (int i = 0; i < edgesArr.length(); i++) {
                JSONArray a = edgesArr.getJSONArray(i);
                rawEdges.add(new Object[]{
                    a.getString(0), a.getString(1), a.getString(2),
                    a.getString(3), a.getDouble(4), a.getDouble(5)
                });
            }

            JSONArray doorDistArr = root.getJSONArray("doorDist");
            for (int i = 0; i < doorDistArr.length(); i++) {
                JSONArray a = doorDistArr.getJSONArray(i);
                rawDoorDist.add(new Object[]{
                    a.getString(0), a.getString(1), a.getDouble(2)
                });
            }

            JSONArray objectsArr = root.getJSONArray("objects");
            for (int i = 0; i < objectsArr.length(); i++) {
                JSONArray a = objectsArr.getJSONArray(i);
                rawObjects.add(new Object[]{ a.getString(0), a.getString(1) });
            }

            return !rawNodes.isEmpty();
        } catch (Exception e) {
            System.out.println("[envManager] Errore caricamento: " + e.getMessage());
            return false;
        }
    }
}