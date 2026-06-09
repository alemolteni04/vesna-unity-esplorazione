package artifacts;

import cartago.*;
import pathfinding.AStarPathfinder;
import pathfinding.AStarPathfinder.EdgeData;

import java.util.List;

/**
 * PathFinderArtifact.java
 *
 * Artefatto CArtAgO che espone il calcolo del cammino minimo (A*)
 * all'agente Jason. Il grafo viene costruito dall'agente passando
 * nodi e archi come parametri, oppure caricato dal file JSON.
 *
 * CICLO DI VITA:
 *   1. L'agente crea l'artefatto con makeArtifact dopo exploration_complete
 *   2. L'agente popola il grafo con addNode/addEdge per ogni belief
 *   3. L'agente chiama computePath(Start, Goal) e legge il risultato
 *      dall'observable property "path"
 *
 * OBSERVABLE PROPERTIES:
 *   path(Start, Goal, [N1, N2, ...], Cost)  — percorso calcolato
 *   ready(false)                             — true quando il grafo è pronto
 *
 * OPERAZIONI:
 *   addNode(Id, Type, Floor, X, Y, Z)
 *   addEdge(From, To, Weight, EdgeType)
 *   computePath(StartId, GoalId)
 *   computePathToArtifact(StartId, ArtifactId, RoomId)
 *   resetGraph()
 */
public class PathFinderArtifact extends Artifact {

    private AStarPathfinder pathfinder;

    // -----------------------------------------------------------------------
    // Inizializzazione
    // -----------------------------------------------------------------------

    void init() {
        pathfinder = new AStarPathfinder();
        defineObsProperty("ready", false);
        defineObsProperty("path",  "none", "none", new Object[0], 0.0);
        log("PathFinderArtifact inizializzato.");
    }

    // -----------------------------------------------------------------------
    // Costruzione del grafo
    // -----------------------------------------------------------------------

    /**
     * Aggiunge un nodo al grafo interno.
     * L'agente chiama questa operazione per ogni belief node(...).
     *
     * Esempio Jason:
     *   addNode(Id, Type, Floor, X, Y, Z) [artifact_name("pathfinder")];
     */
    @OPERATION
    void addNode(String id, String type, int floor,
                 double x, double y, double z) {
        pathfinder.addNode(id, type, floor, x, y, z);
    }

    /**
     * Aggiunge un arco pesato al grafo interno.
     * L'agente chiama questa operazione per ogni belief edge(...).
     *
     * @param from      nodeId sorgente
     * @param to        nodeId destinazione
     * @param weight    distanza NavMesh in metri
     * @param edgeType  tipo arco (DoorFW, DoorBW, Segment, Central)
     *
     * Esempio Jason:
     *   addEdge(From, To, Weight, EdgeType) [artifact_name("pathfinder")];
     */
    @OPERATION
    void addEdge(String from, String to, double weight, String edgeType) {
        pathfinder.addEdge(from, to, weight, edgeType);
    }

    /**
     * Segnala che il grafo è stato popolato completamente.
     * L'agente chiama questa operazione dopo aver aggiunto tutti i nodi/archi.
     */
    @OPERATION
    void graphReady() {
        updateObsProperty("ready", true);
        log("Grafo pronto: " + pathfinder.getNodeIds().size() + " nodi.");
    }

    // -----------------------------------------------------------------------
    // Calcolo del percorso
    // -----------------------------------------------------------------------

    /**
     * Calcola il cammino minimo da startId a goalId con A*.
     * Il risultato viene scritto nell'observable property "path".
     *
     * Esempio Jason:
     *   computePath("Ufficio1", "Laboratorio3") [artifact_name("pathfinder")];
     *   ?path(_, _, Path, Cost);
     *
     * @param startId  nodeId del nodo di partenza (stanza corrente agente)
     * @param goalId   nodeId del nodo destinazione (stanza target)
     */
    @OPERATION
    void computePath(String startId, String goalId) {
        log("Calcolo percorso: " + startId + " → " + goalId);

        List<String> path = pathfinder.computePath(startId, goalId);

        if (path.isEmpty()) {
            log("Nessun percorso trovato da " + startId + " a " + goalId);
            updateObsProperty("path", startId, goalId, new Object[0], 0.0);
            return;
        }

        double cost = pathfinder.pathCost(path);
        Object[] pathArray = path.toArray();

        updateObsProperty("path", startId, goalId, pathArray, cost);

        log("Percorso trovato (" + path.size() + " nodi, " +
            String.format("%.2f", cost) + "m): " + path);
    }

    /**
     * Calcola il cammino minimo verso la stanza in cui si trova un artefatto.
     * Utile quando la destinazione è un oggetto (new_object) invece di una stanza.
     *
     * Esempio Jason:
     *   computePathToArtifact("Ufficio1", "Artefatto ufficio5", "Ufficio5")
     *       [artifact_name("pathfinder")];
     *
     * @param startId     nodeId del nodo di partenza
     * @param artifactId  ID dell'artefatto target (per log)
     * @param roomId      stanza in cui si trova l'artefatto
     */
    @OPERATION
    void computePathToArtifact(String startId,
                                String artifactId,
                                String roomId) {
        log("Calcolo percorso verso artefatto '" + artifactId +
            "' in stanza '" + roomId + "'");
        computePath(startId, roomId);
    }

    /**
     * Azzera il grafo interno.
     * Utile per riesplorazione o cambio edificio.
     */
    @OPERATION
    void resetGraph() {
        pathfinder = new AStarPathfinder();
        updateObsProperty("ready", false);
        updateObsProperty("path", "none", "none", new Object[0], 0.0);
        log("Grafo resettato.");
    }
}
