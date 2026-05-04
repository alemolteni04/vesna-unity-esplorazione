using System.Collections.Generic;
using UnityEngine;

// ============================================================
// TopologicalGraph.cs  — v2
// ============================================================
// LIVELLO 1 (Piano): ogni corridoio ha DUE nodi polo A e B.
//   corridoioX_A ──[central_corridoioX]──► corridoioX_B
//   corridoioX_A ──[fw_porta]──► porta    (distFromA, orderFW)
//   corridoioX_B ──[bw_porta]──► porta    (distFromB, orderBW)
//
// LIVELLO 2 (Stanza): nodo stanza con archi verso porte/varchi.
//   roomNode ──[room_porta]──► porta      (navMeshDist, greedy)
//   Se archi > 3 → NodeType.Complex
// ============================================================

public enum NodeType  { Unknown, CorridorPoleA, CorridorPoleB, Room, Complex }
public enum EdgeState { Discovered, Explored }
public enum DoorState { Open, Closed, Locked }
public enum EdgeType  { Central, DoorFW, DoorBW, RoomDoor, Segment }

// ─────────────────────────────────────────────────────────────
// NODO
// ─────────────────────────────────────────────────────────────
[System.Serializable]
public class GraphNode
{
    public string   id;
    public NodeType type;
    public Vector3  position;
    public bool     fullyExplored;
    public string   corridorId;   // valorizzato solo per poli corridoio
    public string   poleLabel;    // "A" o "B"

    public List<GraphEdge> edges = new List<GraphEdge>();

    public GraphNode(string id, NodeType type, Vector3 position)
    {
        this.id       = id;
        this.type     = type;
        this.position = position;
        fullyExplored = false;
    }

    public bool HasUndiscoveredEdges()
    {
        foreach (var e in edges)
            if (e != null && e.state == EdgeState.Discovered) return true;
        return false;
    }

    public int EdgeCount => edges.Count;
}

// ─────────────────────────────────────────────────────────────
// ARCO
// ─────────────────────────────────────────────────────────────
[System.Serializable]
public class GraphEdge
{
    public string    id;
    public string    fromNodeId;
    public string    toNodeId;
    public EdgeState state;
    public EdgeType  edgeType;
    public DoorState doorState;
    public bool      isPhysical;
    public string    side;
    public float     distFromA;
    public float     distFromB;
    public int       orderFW;
    public int       orderBW;
    public float     navMeshDist;
    public Vector3   position;

    public GraphEdge(string id, string fromNodeId, bool isPhysical,
                     EdgeType edgeType, Vector3 position)
    {
        this.id         = id;
        this.fromNodeId = fromNodeId;
        this.isPhysical = isPhysical;
        this.edgeType   = edgeType;
        this.position   = position;
        this.state      = EdgeState.Discovered;
        this.doorState  = isPhysical ? DoorState.Closed : DoorState.Open;
        this.toNodeId   = null;
    }
}

// ─────────────────────────────────────────────────────────────
// GRAFO
// ─────────────────────────────────────────────────────────────
public class TopologicalGraph
{
    private Dictionary<string, GraphNode> nodes = new Dictionary<string, GraphNode>();
    private Stack<string> explorationStack       = new Stack<string>();

    public string CurrentNodeId { get; private set; }

    // =========================================================
    // AGGIUNTA NODI
    // =========================================================

    public GraphNode AddNode(string id, NodeType type, Vector3 position)
    {
        if (string.IsNullOrEmpty(id))
        {
            Debug.LogWarning("[Graph] AddNode: id nullo/vuoto ignorato.");
            return null;
        }
        if (nodes.ContainsKey(id))
        {
            if (nodes[id].type == NodeType.Unknown && type != NodeType.Unknown)
                nodes[id].type = type;
            return nodes[id];
        }
        var node = new GraphNode(id, type, position);
        nodes[id] = node;
        Debug.Log($"[Graph] Nodo aggiunto: {id} ({type})");
        return node;
    }

    // Crea coppia polo A + polo B + arco centrale (idempotente)
    public (GraphNode poleA, GraphNode poleB) AddCorridorPoles(
        string corridorId, Vector3 posA, Vector3 posB, float centralLength)
    {
        string idA = $"{corridorId}_A";
        string idB = $"{corridorId}_B";

        var nodeA = AddNode(idA, NodeType.CorridorPoleA, posA);
        if (nodeA != null) { nodeA.corridorId = corridorId; nodeA.poleLabel = "A"; }

        var nodeB = AddNode(idB, NodeType.CorridorPoleB, posB);
        if (nodeB != null) { nodeB.corridorId = corridorId; nodeB.poleLabel = "B"; }

        string centralId = $"central_{corridorId}";
        if (nodeA != null && nodeA.edges.Find(e => e?.id == centralId) == null)
        {
            var central = new GraphEdge(centralId, idA, false, EdgeType.Central, posB)
            {
                toNodeId  = idB,
                distFromA = centralLength,
                state     = EdgeState.Discovered
            };
            nodeA.edges.Add(central);
            Debug.Log($"[Graph] Arco centrale: {idA}→{idB} len={centralLength:F1}");
        }

        return (nodeA, nodeB);
    }

    // =========================================================
    // AGGIUNTA ARCHI PORTA — doppio FW (da A) e BW (da B)
    // =========================================================

    public void AddDoorEdges(string corridorId, string doorId, bool isPhysical,
                             Vector3 doorPos, string side,
                             float distFromA, float distFromB, int orderFW)
    {
        string idA = $"{corridorId}_A";
        string idB = $"{corridorId}_B";

        if (!nodes.ContainsKey(idA) || !nodes.ContainsKey(idB))
        {
            Debug.LogWarning($"[Graph] AddDoorEdges: poli {corridorId} non ancora creati!");
            return;
        }

        var nodeA = nodes[idA];
        var nodeB = nodes[idB];

        // Arco FW: polo A → porta
        string fwId = $"fw_{doorId}";
        if (nodeA.edges.Find(e => e?.id == fwId) == null)
        {
            nodeA.edges.Add(new GraphEdge(fwId, idA, isPhysical, EdgeType.DoorFW, doorPos)
            {
                toNodeId = doorId, side = side,
                distFromA = distFromA, distFromB = distFromB, orderFW = orderFW
            });
        }

        // Arco BW: polo B → porta
        string bwId = $"bw_{doorId}";
        if (nodeB.edges.Find(e => e?.id == bwId) == null)
        {
            nodeB.edges.Add(new GraphEdge(bwId, idB, isPhysical, EdgeType.DoorBW, doorPos)
            {
                toNodeId = doorId, side = side,
                distFromA = distFromA, distFromB = distFromB, orderFW = orderFW
            });
        }

        Debug.Log($"[Graph] DoorEdges: {corridorId}↔{doorId} side={side} dA={distFromA:F1} dB={distFromB:F1}");
    }

    // Arco da nodo stanza → porta (scoperta col 360° interno)
    public GraphEdge AddRoomDoorEdge(string fromNodeId, string doorId, bool isPhysical,
                                     Vector3 doorPos, float navMeshDist)
    {
        if (!nodes.ContainsKey(fromNodeId)) return null;
        var node  = nodes[fromNodeId];
        string eid = $"room_{doorId}";

        var existing = node.edges.Find(e => e?.id == eid);
        if (existing != null) return existing;

        var edge = new GraphEdge(eid, fromNodeId, isPhysical, EdgeType.RoomDoor, doorPos)
        {
            navMeshDist = navMeshDist,
            toNodeId    = doorId
        };
        node.edges.Add(edge);

        if (node.type == NodeType.Room && node.EdgeCount > 3)
        {
            node.type = NodeType.Complex;
            Debug.Log($"[Graph] {fromNodeId} → Complex (archi={node.EdgeCount})");
        }

        Debug.Log($"[Graph] RoomDoor: {fromNodeId}→{doorId} navDist={navMeshDist:F1}");
        return edge;
    }

    // =========================================================
    // NAVIGAZIONE (stack DFS)
    // =========================================================

    public void EnterNode(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (!nodes.ContainsKey(nodeId))
        {
            Debug.LogError($"[Graph] EnterNode: nodo '{nodeId}' non esiste!");
            return;
        }
        explorationStack.Push(nodeId);
        CurrentNodeId = nodeId;
        Debug.Log($"[Graph] Enter: {nodeId} (depth={explorationStack.Count})");
    }

    public string Backtrack()
    {
        if (explorationStack.Count == 0) return null;
        explorationStack.Pop();
        if (explorationStack.Count == 0)
        {
            CurrentNodeId = null;
            Debug.Log("[Graph] Stack vuoto.");
            return null;
        }
        CurrentNodeId = explorationStack.Peek();
        Debug.Log($"[Graph] Backtrack → {CurrentNodeId}");
        return CurrentNodeId;
    }

    // =========================================================
    // MARK EXPLORED
    // =========================================================

    public void MarkEdgeExplored(string fromNodeId, string edgeId, string toNodeId = null)
    {
        if (!nodes.ContainsKey(fromNodeId)) return;
        var edge = nodes[fromNodeId].edges.Find(e => e?.id == edgeId);
        if (edge == null) return;
        edge.state = EdgeState.Explored;
        if (toNodeId != null) edge.toNodeId = toNodeId;
        Debug.Log($"[Graph] Explored: {fromNodeId}→{edgeId} (→{edge.toNodeId ?? "?"})");
    }

    public void UpdateDoorState(string edgeId, DoorState state)
    {
        foreach (var node in nodes.Values)
        {
            var edge = node.edges.Find(e => e?.id == edgeId);
            if (edge != null) { edge.doorState = state; return; }
        }
    }

    // =========================================================
    // CALCOLO orderBW al polo B
    // =========================================================

    public void ComputeBackwardOrders(string corridorId)
    {
        string idB = $"{corridorId}_B";
        if (!nodes.ContainsKey(idB)) return;
        var bwEdges = nodes[idB].edges.FindAll(e => e != null && e.edgeType == EdgeType.DoorBW);
        bwEdges.Sort((a, b) => a.distFromB.CompareTo(b.distFromB));
        for (int i = 0; i < bwEdges.Count; i++)
            bwEdges[i].orderBW = i + 1;
        Debug.Log($"[Graph] orderBW calcolati per {idB} ({bwEdges.Count} porte)");
    }

    // =========================================================
    // QUERY
    // =========================================================

    public GraphNode GetNode(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        nodes.TryGetValue(id, out var n);
        return n;
    }

    public IEnumerable<GraphNode> AllNodes() => nodes.Values;

    // ── Prossimo arco in corridoio (polo B, orderBW) ──
    public GraphEdge NextCorridorEdgeToInspect(string poleBNodeId)
    {
        if (!nodes.ContainsKey(poleBNodeId)) return null;
        var list = nodes[poleBNodeId].edges.FindAll(
            e => e != null && e.state == EdgeState.Discovered && e.edgeType == EdgeType.DoorBW);
        if (list.Count == 0) return null;
        list.Sort((a, b) =>
        {
            if (a.orderBW > 0 && b.orderBW > 0) return a.orderBW.CompareTo(b.orderBW);
            return a.distFromB.CompareTo(b.distFromB);
        });
        return list[0];
    }

    // ── Prossimo arco in stanza (greedy NavMesh) ──
    public GraphEdge NextRoomEdgeToInspect(string roomNodeId)
    {
        if (!nodes.ContainsKey(roomNodeId)) return null;
        var list = nodes[roomNodeId].edges.FindAll(
            e => e != null && e.state == EdgeState.Discovered && e.edgeType == EdgeType.RoomDoor);
        if (list.Count == 0) return null;
        list.Sort((a, b) => a.navMeshDist.CompareTo(b.navMeshDist));
        return list[0];
    }

    // ── Fallback: qualsiasi discovered ──
    public GraphEdge NextEdgeToInspect(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId) || !nodes.ContainsKey(nodeId)) return null;
        var node = nodes[nodeId];
        if (node.type == NodeType.CorridorPoleB) return NextCorridorEdgeToInspect(nodeId);
        if (node.type == NodeType.Room || node.type == NodeType.Complex)
            return NextRoomEdgeToInspect(nodeId);
        var any = node.edges.FindAll(e => e != null && e.state == EdgeState.Discovered);
        if (any.Count == 0) return null;
        any.Sort((a, b) => a.distFromB.CompareTo(b.distFromB));
        return any[0];
    }

    // ── Conteggio discovered (debug) ──
    public int GetDiscoveredEdgesCount(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId) || !nodes.ContainsKey(nodeId)) return 0;
        return nodes[nodeId].edges.FindAll(e => e != null && e.state == EdgeState.Discovered).Count;
    }

    public string FindNearestNodeWithDiscoveredEdges(Vector3 agentPos)
    {
        string nearest = null;
        float  minDist = float.MaxValue;
        foreach (var node in nodes.Values)
        {
            if (node == null || !node.HasUndiscoveredEdges()) continue;
            float d = Vector3.Distance(agentPos, node.position);
            if (d < minDist) { minDist = d; nearest = node.id; }
        }
        return nearest;
    }

    public bool IsFullyExplored()
    {
        foreach (var node in nodes.Values)
            if (node != null && node.HasUndiscoveredEdges()) return false;
        return true;
    }
}