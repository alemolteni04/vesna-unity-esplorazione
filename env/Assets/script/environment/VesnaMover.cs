using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using Newtonsoft.Json.Linq;

// ============================================================
// VesnaMover.cs
// ============================================================
// Da mettere sullo stesso GameObject di Vesna, insieme a:
//   - NavMeshAgent
//   - BeliefTransmitter (porta separata, es. 8081)
//
// Riceve MoveTo(nodeId) (chiamato da BeliefTransmitter quando
// arriva "move_to(NodeId)" da JaCaMo), naviga con NavMesh verso
// la posizione del nodo, e quando arriva chiama
// beliefTransmitter.SendCurrentRoom(nodeId) per aggiornare
// current_room in JaCaMo.
//
// Le posizioni dei nodi sono lette dal file building.json
// generato dall'esplorazione (graph_snapshots).
// ============================================================

public class VesnaMover : MonoBehaviour
{
    [Header("Riferimenti")]
    public NavMeshAgent agent;
    public BeliefTransmitter beliefTransmitter;

    [Header("Configurazione")]
    [Tooltip("Distanza entro cui consideriamo il nodo 'raggiunto'")]
    public float arrivalThreshold = 0.5f;

    [Tooltip("Cartella dove si trovano i file *.json del grafo (graph_snapshots)")]
    public string graphSnapshotsFolder =
        @"%LOCALAPPDATA%Low\DefaultCompany\JaCaMoIntegration\graph_snapshots";

    // nodeId -> posizione mondo
    private Dictionary<string, Vector3> nodePositions = new Dictionary<string, Vector3>();

    private string currentTargetNode = null;
    private bool isMoving = false;

    void Awake()
    {
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        if (beliefTransmitter == null) beliefTransmitter = GetComponent<BeliefTransmitter>();

        StartCoroutine(LoadNodePositionsWithRetry());
    }

    private System.Collections.IEnumerator LoadNodePositionsWithRetry()
    {
        while (nodePositions.Count == 0)
        {
            LoadNodePositions();
            if (nodePositions.Count > 0) break;

            Debug.Log("[VesnaMover] building.json non ancora disponibile, riprovo in 3s...");
            yield return new WaitForSeconds(3f);
        }
    }

    void Update()
    {
        if (!isMoving || currentTargetNode == null) return;

        if (!agent.pathPending && agent.remainingDistance <= arrivalThreshold)
        {
            ArrivedAt(currentTargetNode);
        }
    }

    // --------------------------------------------------------
    // Carica le posizioni dei nodi dal building.json più recente
    // --------------------------------------------------------
    private void LoadNodePositions()
    {
        nodePositions.Clear();

        string folder = System.Environment.ExpandEnvironmentVariables(graphSnapshotsFolder);

        if (!Directory.Exists(folder))
        {
            Debug.Log($"[VesnaMover] Cartella grafo non ancora presente: {folder}");
            return;
        }

        // Trova il file .json più recente, escludendo target.json e *_artifacts.json
        string buildingFile = null;
        System.DateTime newest = System.DateTime.MinValue;

        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("target.json", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith("_artifacts.json", System.StringComparison.OrdinalIgnoreCase)) continue;

            var mtime = File.GetLastWriteTimeUtc(file);
            if (mtime > newest)
            {
                newest = mtime;
                buildingFile = file;
            }
        }

        if (buildingFile == null)
        {
            Debug.Log($"[VesnaMover] Nessun building*.json ancora presente in {folder}");
            return;
        }

        Debug.Log($"[VesnaMover] Carico posizioni nodi da: {buildingFile}");

        string json = File.ReadAllText(buildingFile);
        JObject root = JObject.Parse(json);

        foreach (var floor in root["floors"])
        {
            foreach (var node in floor["nodes"])
            {
                string nodeType = node["nodeType"]?.ToString();
                float x = node["x"].Value<float>();
                float y = node["y"].Value<float>();
                float z = node["z"].Value<float>();
                Vector3 pos = new Vector3(x, y, z);

                if (nodeType == "CorridorPoleA" || nodeType == "CorridorPoleB")
                {
                    // Aggiungi sia il nodo reale (es. Corridoio2_A) sia il
                    // nome "corto" del corridoio (es. Corridoio2) -> se non
                    // già presente, cosi' "move_to(Corridoio2)" funziona
                    // anche se Jason manda il pole, ma il nome corto punta
                    // al primo pole trovato.
                    string nodeId = node["nodeId"]?.ToString();
                    string corridorId = node["corridorId"]?.ToString();

                    if (!string.IsNullOrEmpty(nodeId))
                        nodePositions[nodeId] = pos;

                    if (!string.IsNullOrEmpty(corridorId) && !nodePositions.ContainsKey(corridorId))
                        nodePositions[corridorId] = pos;
                }
                else
                {
                    string nodeId = node["nodeId"]?.ToString();
                    if (!string.IsNullOrEmpty(nodeId))
                        nodePositions[nodeId] = pos;
                }
            }
        }

        Debug.Log($"[VesnaMover] Posizioni nodi caricate: {nodePositions.Count}");
          
    
    // AGGIUNTO: carica anche i nodi mancanti da pathfinder_state.json
    LoadFromPathfinderState();
    }
    private void LoadFromPathfinderState()
{
    string path = System.IO.Path.Combine(
        System.Environment.ExpandEnvironmentVariables(graphSnapshotsFolder),
        "pathfinder_state.json");
    if (!File.Exists(path)) return;

    var root = JObject.Parse(File.ReadAllText(path));
    foreach (var node in root["nodes"])
    {
        string nodeId = node[0]?.ToString();
        float x = node[3].Value<float>();
        float y = node[4].Value<float>();
        float z = node[5].Value<float>();
        if (!string.IsNullOrEmpty(nodeId) && !nodePositions.ContainsKey(nodeId))
            nodePositions[nodeId] = new Vector3(x, y, z);
    }
}

    // --------------------------------------------------------
    // Chiamato da BeliefTransmitter quando arriva move_to(NodeId)
    // --------------------------------------------------------
    public void MoveTo(string nodeId)
    {
        if (!nodePositions.TryGetValue(nodeId, out Vector3 targetPos))
        {
            Debug.LogWarning($"[VesnaMover] Nodo '{nodeId}' non trovato nelle posizioni caricate.");
            return;
        }

        Debug.Log($"[VesnaMover] Vado verso {nodeId} @ {targetPos}");
        currentTargetNode = nodeId;
        isMoving = true;
        agent.SetDestination(targetPos);
    }

    // --------------------------------------------------------
    // Arrivo al nodo: notifica JaCaMo aggiornando current_room
    // --------------------------------------------------------
private void ArrivedAt(string nodeId)
{
    Debug.Log($"[VesnaMover] Arrivata a {nodeId}");
    isMoving = false;
    currentTargetNode = null;

    beliefTransmitter?.SendMovementCompleted(nodeId);

    var loc = GetComponent<LocalizationTest>();
    loc?.SetCurrentRoom(nodeId);
}
}