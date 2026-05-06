using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

// ============================================================
// TopologicalGraphDebugger.cs  — v2
// ============================================================
// Visualizza il TopologicalGraph nella scena Unity.
//
// SETUP:
//   1. Attacca sullo stesso GameObject di ExplorationManager
//   2. Premi Play
//   3. Scene View → abilita "Gizmos"
//
// LEGENDA NODI:
//   Giallo    → CorridorPoleA
//   Arancione → CorridorPoleB
//   Verde     → Room
//   Magenta   → Complex
//   Grigio    → Unknown
//   Ciano     → nodo corrente
//
// LEGENDA ARCHI:
//   Bianco       → Central
//   Verde chiaro → DoorFW
//   Arancione    → DoorBW
//   Azzurro      → RoomDoor
//   Rosso        → Explored
// ============================================================

[ExecuteAlways]
public class TopologicalGraphDebugger : MonoBehaviour
{
    [Header("Riferimento (auto-trovato se null)")]
    public ExplorationManager explorationManager;

    [Header("Nodi")]
    public float nodeSphereRadius = 0.3f;
    public bool  showNodeLabels   = true;

    [Header("Archi")]
    public float lineThickness  = 4f;    // spessore linee Handles
    public float yOffset        = 0.15f; // alzati dal pavimento per non sparire sotto NavMesh
    public bool  showEdgeLabels = false;

    [Header("HUD Game View")]
    public bool showHUD = true;

    // ── Colori nodi ──────────────────────────────────────────
    private static readonly Color ColPoleA   = new Color(1f,   0.92f, 0f,   1f);
    private static readonly Color ColPoleB   = new Color(1f,   0.55f, 0f,   1f);
    private static readonly Color ColRoom    = new Color(0.2f, 0.9f,  0.2f, 1f);
    private static readonly Color ColComplex = new Color(0.9f, 0.2f,  0.9f, 1f);
    private static readonly Color ColUnknown = new Color(0.5f, 0.5f,  0.5f, 1f);
    private static readonly Color ColCurrent = new Color(0f,   1f,    1f,   1f);

    // ── Colori archi ─────────────────────────────────────────
    private static readonly Color ColCentral  = Color.white;
    private static readonly Color ColDoorFW   = new Color(0.4f, 1f,   0.4f, 1f);
    private static readonly Color ColDoorBW   = new Color(1f,   0.6f, 0.2f, 1f);
    private static readonly Color ColRoomDoor = new Color(0.4f, 0.8f, 1f,   1f);
    private static readonly Color ColExplored = new Color(1f,   0.2f, 0.2f, 0.8f);

    // ── Reflection ───────────────────────────────────────────
    public TopologicalGraph GetGraph()
    {
        if (explorationManager == null)
            explorationManager = GetComponent<ExplorationManager>();
        if (explorationManager == null) return null;

        var field = typeof(ExplorationManager).GetField("graph",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        return field?.GetValue(explorationManager) as TopologicalGraph;
    }

    public string GetCurrentNodeId()
    {
        if (explorationManager == null) return null;
        var field = typeof(ExplorationManager).GetField("currentNodeId",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        return field?.GetValue(explorationManager) as string;
    }

    // ── Offset Y ─────────────────────────────────────────────
    public Vector3 Up(Vector3 v) => v + Vector3.up * yOffset;

    // ============================================================
    // GIZMOS — sfere nodi
    // ============================================================
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        var graph = GetGraph();
        if (graph == null) return;

        string currentId = GetCurrentNodeId();

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;

            bool isCurrent = node.id == currentId;
            Gizmos.color   = isCurrent ? ColCurrent : NodeColor(node.type);

            float r = isCurrent ? nodeSphereRadius * 1.5f : nodeSphereRadius;
            Gizmos.DrawSphere(Up(node.position), r);
        }
    }

    // ============================================================
    // HANDLES — linee spesse + frecce + label
    // ============================================================
#if UNITY_EDITOR
    [DrawGizmo(GizmoType.Active | GizmoType.NonSelected)]
    static void DrawGraphGizmos(TopologicalGraphDebugger dbg, GizmoType _)
    {
        if (!Application.isPlaying) return;

        var graph = dbg.GetGraph();
        if (graph == null) return;

        string currentId = dbg.GetCurrentNodeId();

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;

            bool isCurrent = node.id == currentId;

            // Label nodo
            if (dbg.showNodeLabels)
            {
                Handles.color = isCurrent ? ColCurrent : NodeColor(node.type);
                string lbl = node.fullyExplored ? $"{node.id} ✓" : node.id;
                Handles.Label(
                    dbg.Up(node.position) + Vector3.up * (dbg.nodeSphereRadius + 0.15f),
                    lbl);
            }

            // Archi
            foreach (var edge in node.edges)
            {
                if (edge == null) continue;

                Color c = edge.state == EdgeState.Explored
                    ? ColExplored
                    : EdgeColor(edge.edgeType);

                Handles.color = c;

                Vector3 from = dbg.Up(node.position);
                Vector3 to   = dbg.Up(edge.position);

                // Se il nodo target esiste nel grafo, usa la sua posizione
                if (!string.IsNullOrEmpty(edge.toNodeId))
                {
                    var toNode = graph.GetNode(edge.toNodeId);
                    if (toNode != null) to = dbg.Up(toNode.position);
                }

                // Linea spessa
                Handles.DrawLine(from, to, dbg.lineThickness);

                // Freccia direzionale a 60% del percorso
                Vector3 mid = Vector3.Lerp(from, to, 0.6f);
                Vector3 dir = (to - from).normalized;
                if (dir != Vector3.zero)
                    Handles.ArrowHandleCap(
                        0, mid,
                        Quaternion.LookRotation(dir),
                        0.25f, EventType.Repaint);

                // Label arco opzionale
                if (dbg.showEdgeLabels)
                {
                    string lbl = edge.id;
                    if (edge.orderFW > 0) lbl += $" fw{edge.orderFW}";
                    if (edge.orderBW > 0) lbl += $" bw{edge.orderBW}";
                    Handles.Label(
                        Vector3.Lerp(from, to, 0.5f) + Vector3.up * 0.25f,
                        lbl);
                }
            }
        }
    }
#endif

    // ============================================================
    // HUD Game View
    // ============================================================
    private void OnGUI()
    {
        if (!showHUD || !Application.isPlaying) return;

        var graph = GetGraph();
        if (graph == null) return;

        string currentId     = GetCurrentNodeId();
        int    nodeCount     = 0;
        int    exploredEdges = 0;
        int    discovered    = 0;

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;
            nodeCount++;
            foreach (var e in node.edges)
            {
                if (e == null) continue;
                if (e.state == EdgeState.Explored)   exploredEdges++;
                if (e.state == EdgeState.Discovered) discovered++;
            }
        }

        GUIStyle lbl = new GUIStyle(GUI.skin.label) { fontSize = 13 };

        GUI.Box(new Rect(10, 10, 270, 125), "");
        GUI.Label(new Rect(16, 14,  258, 22), "── Topological Graph ──",                          lbl);
        GUI.Label(new Rect(16, 34,  258, 20), $"Nodi:             {nodeCount}",                   lbl);
        GUI.Label(new Rect(16, 54,  258, 20), $"Archi Explored:   {exploredEdges}",               lbl);
        GUI.Label(new Rect(16, 74,  258, 20), $"Archi Discovered: {discovered}",                  lbl);
        GUI.Label(new Rect(16, 94,  258, 20), $"Nodo corrente:    {currentId ?? "—"}",            lbl);
        GUI.Label(new Rect(16, 114, 258, 20), $"Completo:         {(graph.IsFullyExplored() ? "SI ✓" : "no")}", lbl);
    }

    // ── Helpers colore ───────────────────────────────────────
    private static Color NodeColor(NodeType t) => t switch
    {
        NodeType.CorridorPoleA => ColPoleA,
        NodeType.CorridorPoleB => ColPoleB,
        NodeType.Room          => ColRoom,
        NodeType.Complex       => ColComplex,
        _                      => ColUnknown
    };

    private static Color EdgeColor(EdgeType t) => t switch
    {
        EdgeType.Central  => ColCentral,
        EdgeType.DoorFW   => ColDoorFW,
        EdgeType.DoorBW   => ColDoorBW,
        EdgeType.RoomDoor => ColRoomDoor,
        _                 => ColUnknown
    };
}