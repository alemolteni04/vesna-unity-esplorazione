#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// ============================================================
// TopologicalGraphWindow.cs  — Editor Only
// ============================================================
// Finestra Unity custom che mostra il TopologicalGraph come
// diagramma 2D interattivo.
//
// COME APRIRLA:
//   Menu Unity → VEsNA → Topological Graph Viewer
//
// FUNZIONALITÀ:
//   - Nodi colorati per tipo con label ID
//   - Archi colorati per tipo con frecce direzionali
//   - Scroll + zoom con rotella mouse
//   - Drag della canvas con click destro / middle mouse
//   - Click su un nodo → pannello dettagli (ID, tipo, archi)
//   - Aggiornamento automatico ogni frame durante il Play
//   - Legenda colori in basso a sinistra
//   - HUD con statistiche in alto
// ============================================================

public class TopologicalGraphWindow : EditorWindow
{
    // ── Apri la finestra ─────────────────────────────────────
    [MenuItem("VEsNA/Topological Graph Viewer")]
    public static void OpenWindow()
    {
        var win = GetWindow<TopologicalGraphWindow>("Graph Viewer");
        win.minSize = new Vector2(600, 400);
        win.Show();
    }

    // ── Colori ───────────────────────────────────────────────
    // Nodi
    private static readonly Color ColPoleA    = new Color(1f,   0.85f, 0f,   1f);
    private static readonly Color ColPoleB    = new Color(1f,   0.50f, 0f,   1f);
    private static readonly Color ColRoom     = new Color(0.2f, 0.85f, 0.3f, 1f);
    private static readonly Color ColComplex  = new Color(0.85f,0.2f,  0.85f,1f);
    private static readonly Color ColUnknown  = new Color(0.55f,0.55f, 0.55f,1f);
    private static readonly Color ColCurrent  = new Color(0f,   0.95f, 1f,   1f);
    private static readonly Color ColSelected = new Color(1f,   1f,    1f,   1f);

    // Archi
    private static readonly Color ColCentral  = new Color(0.9f, 0.9f,  0.9f, 1f);
    private static readonly Color ColDoorFW   = new Color(0.3f, 1f,    0.3f, 1f);
    private static readonly Color ColDoorBW   = new Color(1f,   0.55f, 0.1f, 1f);
    private static readonly Color ColRoomDoor = new Color(0.3f, 0.75f, 1f,   1f);
    private static readonly Color ColExplored = new Color(1f,   0.25f, 0.25f,0.85f);
    private static readonly Color ColSegment  = new Color(0.6f, 0.6f,  0.6f, 1f);

    // Sfondo e UI
    private static readonly Color ColBg       = new Color(0.13f, 0.13f, 0.15f, 1f);
    private static readonly Color ColGrid     = new Color(0.22f, 0.22f, 0.25f, 1f);
    private static readonly Color ColPanel    = new Color(0.08f, 0.08f, 0.10f, 0.92f);

    // ── Stato canvas ─────────────────────────────────────────
    private Vector2 canvasOffset = Vector2.zero;
    private float   zoom         = 1f;
    private const float ZoomMin  = 0.3f;
    private const float ZoomMax  = 3f;

    // ── Layout nodi (posizioni 2D calcolate) ─────────────────
    private Dictionary<string, Vector2> nodePositions = new Dictionary<string, Vector2>();
    private bool layoutDirty = true;

    // ── Interazione ──────────────────────────────────────────
    private string selectedNodeId = null;
    private bool   isDraggingCanvas = false;
    private Vector2 lastMousePos;

    // ── Riferimento manager ──────────────────────────────────
    private ExplorationManager manager;
    private TopologicalGraph   lastGraph;
    private int                lastNodeCount = 0;

    // ── Dimensioni nodi ──────────────────────────────────────
    private const float NODE_W = 130f;
    private const float NODE_H = 36f;
    private const float NODE_R = 8f;   // corner radius

    // ====================================================
    // UNITY EDITOR LIFECYCLE
    // ====================================================
    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
        canvasOffset = new Vector2(position.width * 0.5f, position.height * 0.5f);
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        // Aggiorna durante il Play
        if (Application.isPlaying)
            Repaint();
    }

    // ====================================================
    // MAIN GUI
    // ====================================================
    private void OnGUI()
    {
        // Trova manager
        if (manager == null)
            manager = FindFirstObjectByType<ExplorationManager>();

        var graph = GetGraph();

        // ── Sfondo ──
        EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), ColBg);

        if (graph == null)
        {
            DrawNoGraph();
            return;
        }

        // Ricalcola layout se il grafo è cambiato
        int nodeCount = CountNodes(graph);
        if (layoutDirty || nodeCount != lastNodeCount)
        {
            ComputeLayout(graph);
            lastNodeCount = nodeCount;
            layoutDirty   = false;
        }

        // ── Input ──
        HandleInput(graph);

        // ── Disegno ──
        DrawGrid();
        DrawEdges(graph);
        DrawNodes(graph);
        DrawHUD(graph);
        DrawLegend();
        DrawDetailPanel(graph);
        DrawControls();
    }

    // ====================================================
    // LAYOUT — proietta le posizioni 3D Unity in 2D
    // ====================================================
    private void ComputeLayout(TopologicalGraph graph)
    {
        nodePositions.Clear();

        // Usa le posizioni X/Z reali di Unity scalate per il layout 2D
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;
            if (node.position.x < minX) minX = node.position.x;
            if (node.position.x > maxX) maxX = node.position.x;
            if (node.position.z < minZ) minZ = node.position.z;
            if (node.position.z > maxZ) maxZ = node.position.z;
        }

        float rangeX = Mathf.Max(maxX - minX, 1f);
        float rangeZ = Mathf.Max(maxZ - minZ, 1f);

        float canvasW = position.width  * 0.7f;
        float canvasH = position.height * 0.7f;
        float scale   = Mathf.Min(canvasW / rangeX, canvasH / rangeZ) * 0.8f;
        scale = Mathf.Clamp(scale, 40f, 120f);

        float centerX = (minX + maxX) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;
            float x =  (node.position.x - centerX) * scale;
            float y = -(node.position.z - centerZ) * scale; // Z → Y (flip per convenzione schermo)
            nodePositions[node.id] = new Vector2(x, y);
        }

        // ── Separazione anti-sovrapposizione ─────────────────────────────
        // Minima distanza tra centri per evitare che i rettangoli si tocchino
        const float minDistX = NODE_W + 20f;
        const float minDistY = NODE_H + 14f;
        const int   iterations = 80;

        var ids = new List<string>(nodePositions.Keys);
        for (int iter = 0; iter < iterations; iter++)
        {
            bool moved = false;
            for (int i = 0; i < ids.Count; i++)
            {
                for (int j = i + 1; j < ids.Count; j++)
                {
                    Vector2 pi = nodePositions[ids[i]];
                    Vector2 pj = nodePositions[ids[j]];
                    float dx = Mathf.Abs(pi.x - pj.x);
                    float dy = Mathf.Abs(pi.y - pj.y);

                    if (dx < minDistX && dy < minDistY)
                    {
                        // Determina l'asse di sovrapposizione minore e spingi su quello
                        float overlapX = minDistX - dx;
                        float overlapY = minDistY - dy;
                        Vector2 push;

                        if (overlapY <= overlapX)
                        {
                            // spingere verticalmente costa meno
                            float sign = (pi.y >= pj.y) ? 1f : -1f;
                            push = new Vector2(0f, sign * overlapY * 0.5f);
                        }
                        else
                        {
                            float sign = (pi.x >= pj.x) ? 1f : -1f;
                            push = new Vector2(sign * overlapX * 0.5f, 0f);
                        }

                        nodePositions[ids[i]] = pi + push;
                        nodePositions[ids[j]] = pj - push;
                        moved = true;
                    }
                }
            }
            if (!moved) break;
        }
    }

    // ====================================================
    // INPUT
    // ====================================================
    private void HandleInput(TopologicalGraph graph)
    {
        var e = Event.current;

        // Zoom con rotella
        if (e.type == EventType.ScrollWheel)
        {
            float delta = -e.delta.y * 0.05f;
            zoom = Mathf.Clamp(zoom + delta, ZoomMin, ZoomMax);
            e.Use();
            Repaint();
        }

        // Drag canvas con middle mouse o tasto destro
        if (e.type == EventType.MouseDown &&
            (e.button == 2 || e.button == 1))
        {
            isDraggingCanvas = true;
            lastMousePos     = e.mousePosition;
            e.Use();
        }
        if (e.type == EventType.MouseUp &&
            (e.button == 2 || e.button == 1))
        {
            isDraggingCanvas = false;
            e.Use();
        }
        if (e.type == EventType.MouseDrag && isDraggingCanvas)
        {
            canvasOffset += e.mousePosition - lastMousePos;
            lastMousePos  = e.mousePosition;
            e.Use();
            Repaint();
        }

        // Click su nodo (selezione)
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            selectedNodeId = null;
            foreach (var node in graph.AllNodes())
            {
                if (node == null) continue;
                Rect r = GetNodeRect(node.id);
                if (r.Contains(e.mousePosition))
                {
                    selectedNodeId = node.id;
                    e.Use();
                    break;
                }
            }
            Repaint();
        }
    }

    // ====================================================
    // DISEGNO GRIGLIA
    // ====================================================
    private void DrawGrid()
    {
        float gridSize = 60f * zoom;
        float offX     = canvasOffset.x % gridSize;
        float offY     = canvasOffset.y % gridSize;

        Handles.color = ColGrid;
        for (float x = offX; x < position.width;  x += gridSize)
            Handles.DrawLine(new Vector3(x, 0), new Vector3(x, position.height));
        for (float y = offY; y < position.height; y += gridSize)
            Handles.DrawLine(new Vector3(0, y), new Vector3(position.width, y));
    }

    // ====================================================
    // DISEGNO ARCHI
    // ====================================================
    private void DrawEdges(TopologicalGraph graph)
    {
        string currentId = GetCurrentNodeId();

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;
            if (!nodePositions.ContainsKey(node.id)) continue;

            foreach (var edge in node.edges)
            {
                if (edge == null) continue;

                // Punto di partenza: centro del nodo
                Vector2 fromScreen = ToScreen(nodePositions[node.id]);

                // Punto di arrivo: nodo target se esiste, altrimenti posizione edge
                Vector2 toScreen;
                if (!string.IsNullOrEmpty(edge.toNodeId) &&
                    nodePositions.ContainsKey(edge.toNodeId))
                {
                    toScreen = ToScreen(nodePositions[edge.toNodeId]);
                }
                else if (nodePositions.ContainsKey(edge.id))
                {
                    toScreen = ToScreen(nodePositions[edge.id]);
                }
                else
                {
                    // Punto arco non ancora nel grafo → skip
                    continue;
                }

                Color c = edge.state == EdgeState.Explored
                    ? ColExplored
                    : EdgeColor(edge.edgeType);

                // Spessore in base al tipo
                float thickness = edge.state == EdgeState.Explored ? 2f : 3f;
                if (edge.edgeType == EdgeType.Central) thickness = 4f;

                // Curva Bezier per archi doppi (FW e BW sullo stesso corridoio)
                // offset laterale per non sovrapporli
                Vector2 dir    = (toScreen - fromScreen).normalized;
                Vector2 perp   = new Vector2(-dir.y, dir.x);
                float   curveOff = GetCurveOffset(edge.edgeType);

                Vector2 p1 = fromScreen + perp * curveOff;
                Vector2 p2 = toScreen   + perp * curveOff;

                Handles.color = c;
                Handles.DrawBezier(
                    p1, p2,
                    p1 + dir * 40f,
                    p2 - dir * 40f,
                    c, null, thickness);

                // Label arco (opzionale, piccola)
                if (zoom > 0.7f)
                {
                    Vector2 mid = Vector2.Lerp(p1, p2, 0.5f) + perp * 12f;
                    string  lbl = BuildEdgeLabel(edge);
                    if (!string.IsNullOrEmpty(lbl))
                    {
                        GUI.color = c;
                        GUI.Label(new Rect(mid.x - 30, mid.y - 10, 80, 20),
                                  lbl, MiniStyle());
                        GUI.color = Color.white;
                    }
                }
            }
        }
    }

    private float GetCurveOffset(EdgeType t)
    {
        return t switch
        {
            EdgeType.DoorFW  =>  14f,
            EdgeType.DoorBW  => -14f,
            _                =>   0f
        };
    }

    private string BuildEdgeLabel(GraphEdge edge)
    {
        var parts = new List<string>();
        if (edge.orderFW > 0) parts.Add($"fw{edge.orderFW}");
        if (edge.orderBW > 0) parts.Add($"bw{edge.orderBW}");
        if (edge.distFromA > 0 && edge.edgeType == EdgeType.DoorFW)
            parts.Add($"{edge.distFromA:F1}m");
        return string.Join(" ", parts);
    }

    // ====================================================
    // DISEGNO NODI
    // ====================================================
    private void DrawNodes(TopologicalGraph graph)
    {
        string currentId = GetCurrentNodeId();

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;
            if (!nodePositions.ContainsKey(node.id)) continue;

            Rect   r         = GetNodeRect(node.id);
            bool   isCurrent = node.id == currentId;
            bool   isSelected= node.id == selectedNodeId;
            Color  fillColor = isCurrent  ? ColCurrent  :
                               isSelected ? ColSelected  :
                               NodeColor(node.type);

            // Ombra
            EditorGUI.DrawRect(new Rect(r.x + 3, r.y + 3, r.width, r.height),
                               new Color(0, 0, 0, 0.4f));

            // Fill nodo
            EditorGUI.DrawRect(r, fillColor);

            // Bordo selezione / corrente
            if (isCurrent || isSelected)
            {
                Color borderCol = isCurrent ? Color.cyan : Color.white;
                DrawBorder(r, borderCol, isCurrent ? 3f : 2f);
            }

            // Icona tipo (piccolo quadratino colorato a sinistra)
            EditorGUI.DrawRect(new Rect(r.x + 4, r.y + 8, 6, r.height - 16),
                               TypeAccentColor(node.type));

            // Label ID (troncato se troppo lungo)
            string label = TruncateLabel(node.id, 16);
            if (node.fullyExplored) label += " ✓";

            GUIStyle style = new GUIStyle(EditorStyles.label)
            {
                fontSize  = 11,
                fontStyle = isCurrent ? FontStyle.Bold : FontStyle.Normal,
                normal    = { textColor = isCurrent || isSelected
                                ? Color.black
                                : new Color(0.05f, 0.05f, 0.05f) },
                alignment = TextAnchor.MiddleCenter,
                clipping  = TextClipping.Clip
            };

            GUI.Label(new Rect(r.x + 12, r.y, r.width - 14, r.height), label, style);
        }
    }

    private void DrawBorder(Rect r, Color c, float thickness)
    {
        Handles.color = c;
        Handles.DrawSolidRectangleWithOutline(r, Color.clear, c);
    }

    // ====================================================
    // HUD STATISTICHE
    // ====================================================
    private void DrawHUD(TopologicalGraph graph)
    {
        string currentId = GetCurrentNodeId();
        int nodes = 0, explored = 0, discovered = 0;

        foreach (var node in graph.AllNodes())
        {
            if (node == null) continue;
            nodes++;
            foreach (var e in node.edges)
            {
                if (e == null) continue;
                if (e.state == EdgeState.Explored)   explored++;
                if (e.state == EdgeState.Discovered) discovered++;
            }
        }

        float w = 320f, h = 72f;
        EditorGUI.DrawRect(new Rect(10, 10, w, h), ColPanel);
        DrawBorder(new Rect(10, 10, w, h), new Color(0.3f,0.3f,0.4f), 1f);

        GUIStyle s = HUDStyle();
        GUI.Label(new Rect(18, 14, w, 18), "TOPOLOGICAL GRAPH VIEWER", BoldHUDStyle());
        GUI.Label(new Rect(18, 32, 160, 16), $"Nodi:  {nodes}   Explored: {explored}   Discovered: {discovered}", s);
        GUI.Label(new Rect(18, 50, 300, 16),
            $"Nodo corrente: {(string.IsNullOrEmpty(currentId) ? "—" : currentId)}", s);
        GUI.Label(new Rect(18, 68, 300, 14),
            $"Grafo completo: {(graph.IsFullyExplored() ? "✓ SI" : "no")}   Zoom: {zoom:F2}x", s);
    }

    // ====================================================
    // LEGENDA
    // ====================================================
    private void DrawLegend()
    {
        float x = 10f;
        float y = position.height - 175f;
        float w = 185f, h = 168f;

        EditorGUI.DrawRect(new Rect(x, y, w, h), ColPanel);
        DrawBorder(new Rect(x, y, w, h), new Color(0.3f,0.3f,0.4f), 1f);

        float cx = x + 10f;
        float cy = y + 8f;

        GUIStyle s  = LegendStyle();
        GUIStyle hd = BoldHUDStyle();
        GUI.Label(new Rect(cx, cy, 170, 16), "LEGENDA", hd); cy += 18f;

        // Nodi
        GUI.Label(new Rect(cx, cy, 170, 13), "── Nodi ──", s); cy += 14f;
        DrawLegendEntry(ref cx, ref cy, s, ColPoleA,   "Polo A corridoio");
        DrawLegendEntry(ref cx, ref cy, s, ColPoleB,   "Polo B corridoio");
        DrawLegendEntry(ref cx, ref cy, s, ColRoom,    "Stanza");
        DrawLegendEntry(ref cx, ref cy, s, ColComplex, "Complessa (>3 archi)");
        DrawLegendEntry(ref cx, ref cy, s, ColCurrent, "Posizione corrente");
        cy += 4f;

        // Archi
        GUI.Label(new Rect(cx, cy, 170, 13), "── Archi ──", s); cy += 14f;
        DrawLegendLine(ref cx, ref cy, s, ColCentral,  "Centrale A→B");
        DrawLegendLine(ref cx, ref cy, s, ColDoorFW,   "Porta FW (da A)");
        DrawLegendLine(ref cx, ref cy, s, ColDoorBW,   "Porta BW (da B)");
        DrawLegendLine(ref cx, ref cy, s, ColRoomDoor, "Porta stanza");
        DrawLegendLine(ref cx, ref cy, s, ColExplored, "Explored");
    }

    private void DrawLegendEntry(ref float cx, ref float cy,
                                  GUIStyle s, Color col, string label)
    {
        EditorGUI.DrawRect(new Rect(cx, cy + 2, 12, 10), col);
        GUI.Label(new Rect(cx + 16, cy, 160, 14), label, s);
        cy += 14f;
    }

    private void DrawLegendLine(ref float cx, ref float cy,
                                 GUIStyle s, Color col, string label)
    {
        Handles.color = col;
        Handles.DrawLine(new Vector3(cx, cy + 7), new Vector3(cx + 12, cy + 7), 3f);
        GUI.Label(new Rect(cx + 16, cy, 160, 14), label, s);
        cy += 14f;
    }

    // ====================================================
    // PANNELLO DETTAGLI NODO SELEZIONATO
    // ====================================================
    private void DrawDetailPanel(TopologicalGraph graph)
    {
        if (string.IsNullOrEmpty(selectedNodeId)) return;
        var node = graph.GetNode(selectedNodeId);
        if (node == null) return;

        float pw = 230f;
        float ph = Mathf.Min(280f, 60f + node.edges.Count * 18f);
        float px = position.width  - pw - 10f;
        float py = 10f;

        EditorGUI.DrawRect(new Rect(px, py, pw, ph), ColPanel);
        DrawBorder(new Rect(px, py, pw, ph), ColSelected, 1.5f);

        float lx = px + 10f;
        float ly = py + 8f;
        GUIStyle s  = LegendStyle();
        GUIStyle hd = BoldHUDStyle();

        GUI.Label(new Rect(lx, ly, pw - 12, 16), "NODO SELEZIONATO", hd); ly += 18f;
        GUI.Label(new Rect(lx, ly, pw - 12, 14), $"ID:   {node.id}", s);   ly += 14f;
        GUI.Label(new Rect(lx, ly, pw - 12, 14), $"Tipo: {node.type}", s); ly += 14f;
        GUI.Label(new Rect(lx, ly, pw - 12, 14),
            $"Pos:  ({node.position.x:F1}, {node.position.z:F1})", s); ly += 14f;
        GUI.Label(new Rect(lx, ly, pw - 12, 14),
            $"Completato: {(node.fullyExplored ? "✓ sì" : "no")}", s); ly += 18f;

        GUI.Label(new Rect(lx, ly, pw - 12, 14), $"Archi ({node.edges.Count}):", hd); ly += 16f;

        foreach (var edge in node.edges)
        {
            if (edge == null) continue;
            Color ec = edge.state == EdgeState.Explored ? ColExplored : EdgeColor(edge.edgeType);
            GUI.color = ec;
            string stateIcon = edge.state == EdgeState.Explored ? "✓" : "○";
            string lbl = $"  {stateIcon} {TruncateLabel(edge.id, 22)}";
            GUI.Label(new Rect(lx, ly, pw - 12, 14), lbl, s);
            GUI.color = Color.white;
            ly += 14f;

            if (ly > py + ph - 10f) break;
        }
    }

    // ====================================================
    // CONTROLLI (pulsanti in basso a destra)
    // ====================================================
    private void DrawControls()
    {
        float bw = 110f, bh = 24f;
        float bx = position.width  - bw - 10f;
        float by = position.height - bh - 10f;

        if (GUI.Button(new Rect(bx, by, bw, bh), "Reset Vista"))
        {
            canvasOffset = new Vector2(position.width * 0.5f, position.height * 0.5f);
            zoom         = 1f;
            layoutDirty  = true;
        }

        if (GUI.Button(new Rect(bx - bw - 8f, by, bw, bh), "Ricalcola Layout"))
            layoutDirty = true;
    }

    // ====================================================
    // MESSAGGIO NESSUN GRAFO
    // ====================================================
    private void DrawNoGraph()
    {
        GUIStyle s = new GUIStyle(EditorStyles.label)
        {
            fontSize  = 14,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = new Color(0.6f, 0.6f, 0.6f) }
        };

        string msg = Application.isPlaying
            ? "ExplorationManager non trovato nella scena."
            : "Premi Play per visualizzare il grafo.";

        GUI.Label(new Rect(0, 0, position.width, position.height), msg, s);
    }

    // ====================================================
    // HELPER — coordinate
    // ====================================================
    private Vector2 ToScreen(Vector2 graphPos)
    {
        return graphPos * zoom + canvasOffset;
    }

    private Rect GetNodeRect(string nodeId)
    {
        if (!nodePositions.ContainsKey(nodeId))
            return Rect.zero;
        Vector2 center = ToScreen(nodePositions[nodeId]);
        float w = NODE_W * Mathf.Clamp(zoom, 0.5f, 1.5f);
        float h = NODE_H * Mathf.Clamp(zoom, 0.5f, 1.5f);
        return new Rect(center.x - w * 0.5f, center.y - h * 0.5f, w, h);
    }

    // ====================================================
    // HELPER — reflection grafo
    // ====================================================
    private TopologicalGraph GetGraph()
    {
        if (manager == null) return null;
        if (!Application.isPlaying) return null;
        var f = typeof(ExplorationManager).GetField("graph",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        return f?.GetValue(manager) as TopologicalGraph;
    }

    private string GetCurrentNodeId()
    {
        if (manager == null) return null;
        var f = typeof(ExplorationManager).GetField("currentNodeId",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        return f?.GetValue(manager) as string;
    }

    private int CountNodes(TopologicalGraph g)
    {
        int n = 0;
        foreach (var _ in g.AllNodes()) n++;
        return n;
    }

    // ====================================================
    // HELPER — colori e stili
    // ====================================================
    private static Color NodeColor(NodeType t) => t switch
    {
        NodeType.CorridorPoleA => ColPoleA,
        NodeType.CorridorPoleB => ColPoleB,
        NodeType.Room          => ColRoom,
        NodeType.Complex       => ColComplex,
        _                      => ColUnknown
    };

    private static Color TypeAccentColor(NodeType t) => t switch
    {
        NodeType.CorridorPoleA => new Color(0.8f, 0.5f, 0f),
        NodeType.CorridorPoleB => new Color(0.7f, 0.3f, 0f),
        NodeType.Room          => new Color(0.1f, 0.6f, 0.1f),
        NodeType.Complex       => new Color(0.6f, 0.1f, 0.6f),
        _                      => new Color(0.3f, 0.3f, 0.3f)
    };

    private static Color EdgeColor(EdgeType t) => t switch
    {
        EdgeType.Central  => ColCentral,
        EdgeType.DoorFW   => ColDoorFW,
        EdgeType.DoorBW   => ColDoorBW,
        EdgeType.RoomDoor => ColRoomDoor,
        EdgeType.Segment  => ColSegment,
        _                 => ColUnknown
    };

    private static string TruncateLabel(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private static GUIStyle HUDStyle() => new GUIStyle(EditorStyles.label)
    {
        fontSize = 11,
        normal   = { textColor = new Color(0.75f, 0.75f, 0.8f) }
    };

    private static GUIStyle BoldHUDStyle() => new GUIStyle(EditorStyles.label)
    {
        fontSize  = 11,
        fontStyle = FontStyle.Bold,
        normal    = { textColor = new Color(0.9f, 0.9f, 1f) }
    };

    private static GUIStyle LegendStyle() => new GUIStyle(EditorStyles.label)
    {
        fontSize = 10,
        normal   = { textColor = new Color(0.75f, 0.75f, 0.78f) }
    };

    private static GUIStyle MiniStyle() => new GUIStyle(EditorStyles.label)
    {
        fontSize  = 9,
        fontStyle = FontStyle.Bold,
        normal    = { textColor = Color.white }
    };
}
#endif