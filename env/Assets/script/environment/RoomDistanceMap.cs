using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// RoomDistanceMap.cs
// ============================================================
// Calcola le distanze NavMesh tra stanze adiacenti (archi Room)
// e distanze fisse tra poli corridoio adiacenti (archi Segment).
// ============================================================

[System.Serializable]
public struct RoomDistancePair
{
    public string roomA;
    public string roomB;
    public int    floor;
    public float  distance;
}

public class RoomDistanceMap : MonoBehaviour
{
    public List<RoomDistancePair> pairs = new List<RoomDistancePair>();

    public void Compute(Dictionary<int, TopologicalGraph> allGraphs)
    {
        pairs.Clear();

        foreach (var kv in allGraphs)
        {
            int floor = kv.Key;
            var graph  = kv.Value;

            foreach (var node in graph.AllNodes())
            {
                foreach (var edge in node.edges)
                {
                    if (edge == null) continue;

                    // Segmento corridoio→corridoio: distanza fissa 0.5m tra i due poli
                    if (edge.edgeType == EdgeType.Segment)
                    {
                        if (!string.IsNullOrEmpty(edge.fromNodeId) &&
                            !string.IsNullOrEmpty(edge.toNodeId)   &&
                            edge.toNodeId != "unknown")
                        {
                            pairs.Add(new RoomDistancePair {
                                roomA    = edge.fromNodeId,
                                roomB    = edge.toNodeId,
                                floor    = floor,
                                distance = edge.distFromA
                            });
                        }
                        continue;
                    }

                    // Solo archi Room diretti stanza→stanza adiacente
                    if (edge.edgeType != EdgeType.Door) continue;
                    if (string.IsNullOrEmpty(edge.toNodeId) ||
                        edge.toNodeId == "unknown") continue;

                    var toNode = graph.GetNode(edge.toNodeId);
                    if (toNode == null) continue;

                    float dist = NavMeshDistance(node.position, toNode.position);
                    if (dist < 0f) continue;

                    pairs.Add(new RoomDistancePair {
                        roomA    = edge.fromNodeId,
                        roomB    = edge.toNodeId,
                        floor    = floor,
                        distance = dist
});
                }
            }

            Debug.Log($"[RoomDistMap] Piano {floor}: {pairs.Count} coppie adiacenti");
        }
    }

    private float NavMeshDistance(Vector3 a, Vector3 b)
    {
        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path)) return -1f;
        if (path.status == NavMeshPathStatus.PathInvalid)          return -1f;

        float d = 0f;
        for (int i = 1; i < path.corners.Length; i++)
            d += Vector3.Distance(path.corners[i-1], path.corners[i]);
        return d;
    }
}