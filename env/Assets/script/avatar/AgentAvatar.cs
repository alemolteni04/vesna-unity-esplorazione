using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using script.core.model.io;
using script.core.util;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using WebSocketSharp;
using System.IO;
using System.Collections;
using Newtonsoft.Json.Linq;

public abstract class AgentAvatar : AbstractMasElement
{
    public string agentFile; 
    public AgentBeliefs agentBeliefs;
    public GameObject[] focusedArtifacts;
    public List<GoalEnum> goals;
    protected TextMeshPro nameTextMeshPro;
    protected NavMeshAgent agent;

    // Variabili per i nodi topologici di Vesna
    protected Dictionary<string, Vector3> nodePositions = new Dictionary<string, Vector3>();
    private string graphSnapshotsFolder = @"%USERPROFILE%\AppData\LocalLow\DefaultCompany\JaCaMoIntegration\graph_snapshots";

    protected virtual void Awake()
    {
        print($"[AgentAvatar.Awake] tipo reale: {this.GetType().Name}");
        initializeWebSocketConnection(OnMessage);
        nameTextMeshPro = GetComponentInChildren<TextMeshPro>();
        agent = GetComponent<NavMeshAgent>();

        if (nameTextMeshPro != null)
        {
            nameTextMeshPro.text = name;
        }

        // Carica i nodi dal JSON
        StartCoroutine(LoadNodePositionsWithRetry());
    }
    
    protected void HandleArtifactGrab(GameObject artifact)
    {
        if (artifact != null)
        {
            var artifactSocket = this.transform.Find("Body/artifactHolder");
            artifact.transform.SetParent(artifactSocket);
            artifact.transform.localPosition = Vector3.zero; 
            artifact.transform.localRotation = Quaternion.identity; 
            print("Holding artifact: " + artifact.name);
        }
    }
    
    protected void HandleArtifactRelease(GameObject artifact, GameObject snapPoint)
    {
        if (artifact != null && snapPoint != null)
        {
            artifact.transform.SetParent(null); 
            var surfaceCollider = snapPoint.GetComponent<Collider>();
            var movingRenderer = artifact.GetComponent<Renderer>();
            var newPos = snapPoint.transform.position; 
            var surfaceTopY = surfaceCollider.bounds.max.y;
            var movingBottomY = movingRenderer.bounds.min.y;
            var yOffset = surfaceTopY - movingBottomY;
            newPos.y += yOffset;
            artifact.transform.position = newPos;
            artifact.transform.rotation = snapPoint.transform.rotation;
        }
    }

    public GameObject[] FocusedArtifacts { get => focusedArtifacts; set => focusedArtifacts = value; }
    public virtual AgentBeliefs AgentBeliefs => agentBeliefs;
    public string AgentFile => agentFile;
    public List<GoalEnum> Goals => goals;
    public string ArtifactToReach { get; set; }
    public string JaCaMoAgentClassPath { get; set; }

    public void ReachDestination(string dest)
    {
        agent.isStopped = false;

        // 1. Cerca oggetto fisico 3D
        var destObject = GameObject.Find(dest);
        if (destObject != null)
        {
            agent.SetDestination(destObject.transform.position);
            return;
        }
        
        // 2. Se non lo trova, cerca nei nodi JSON (Correzione per Vesna)
        if (nodePositions != null && nodePositions.TryGetValue(dest, out Vector3 targetPos))
        {
            agent.SetDestination(targetPos);
            return;
        }

        Debug.LogError($"Destination '{dest}' is null (non trovato né come oggetto né come nodo).");
    }

    public void SendMessageToJaCaMoBrain( string message )
    {
        wsChannel.sendMessage(message);
    }

    protected virtual void OnMessage(object sender, MessageEventArgs e)
    {
        var data = e.Data;
        try
        {
            var message = JsonConvert.DeserializeObject<WsMessage>(data);
            switch (message.Type)
            {
                case MessageTypes.WsInitialization:
                    print("Connection established for " + objInUse.name);
                    break;
                case MessageTypes.Walk:
                    var walkData = message.Data.ToObject<WalkData>();
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        ReachDestination( walkData.Target );
                    });
                    break;
            }
        }
        catch (Exception)
        {
            Debug.LogError("Message could not be converted.");
        }
    }

    // --- METODI PER LEGGERE IL JSON DEI NODI ---
    private IEnumerator LoadNodePositionsWithRetry()
    {
        while (nodePositions.Count == 0)
        {
            LoadNodePositions();
            if (nodePositions.Count > 0) break;
            yield return new WaitForSeconds(3f);
        }
    }

    private void LoadNodePositions()
    {
        nodePositions.Clear();
        string folder = System.Environment.ExpandEnvironmentVariables(graphSnapshotsFolder);
        if (!Directory.Exists(folder)) return;

        string buildingFile = null;
        DateTime newest = DateTime.MinValue;

        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("target.json", StringComparison.OrdinalIgnoreCase) || 
                name.Equals("pathfinder_state.json", StringComparison.OrdinalIgnoreCase) || 
                name.EndsWith("_artifacts.json", StringComparison.OrdinalIgnoreCase)) continue;

            var mtime = File.GetLastWriteTimeUtc(file);
            if (mtime > newest)
            {
                newest = mtime;
                buildingFile = file;
            }
        }

        if (buildingFile == null) return;

        string json = File.ReadAllText(buildingFile);
        JObject root = JObject.Parse(json);

        foreach (var floor in root["floors"])
        {
            foreach (var node in floor["nodes"])
            {
                float x = node["x"].Value<float>();
                float y = node["y"].Value<float>();
                float z = node["z"].Value<float>();
                Vector3 pos = new Vector3(x, y, z);
                
                string nodeId = node["nodeId"]?.ToString();
                if (!string.IsNullOrEmpty(nodeId)) nodePositions[nodeId] = pos;

                string corridorId = node["corridorId"]?.ToString();
                if (!string.IsNullOrEmpty(corridorId) && !nodePositions.ContainsKey(corridorId))
                    nodePositions[corridorId] = pos;
            }
        }
        LoadFromPathfinderState();
        Debug.Log($"[AgentAvatar] nodePositions caricati: {nodePositions.Count}");
        foreach (var k in nodePositions.Keys)
        Debug.Log($"[AgentAvatar] nodo: {k}");
    }

    private void LoadFromPathfinderState()
    {
        string path = Path.Combine(System.Environment.ExpandEnvironmentVariables(graphSnapshotsFolder), "pathfinder_state.json");
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
}