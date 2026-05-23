using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// DoorDistanceMap.cs
// ============================================================
// Calcola le distanze NavMesh tra stanze adiacenti (archi Door)
// e distanze fisse tra poli corridoio adiacenti (archi Segment).
// ============================================================

[System.Serializable]
public struct DoorDistancePair
{
    public string doorA;
    public string doorB;
    public int    floor;
    public float  distance;
}

public class DoorDistanceMap : MonoBehaviour
{
    public List<DoorDistancePair> pairs = new List<DoorDistancePair>();

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
                            pairs.Add(new DoorDistancePair {
                                doorA    = edge.fromNodeId,
                                doorB    = edge.toNodeId,
                                floor    = floor,
                                distance = 0.5f
                            });
                        }
                        continue;
                    }

                    // Solo archi Door diretti stanza→stanza adiacente
                    if (edge.edgeType != EdgeType.Door) continue;
                    if (string.IsNullOrEmpty(edge.toNodeId) ||
                        edge.toNodeId == "unknown") continue;

                    float dist = NavMeshDistance(node.position, edge.position);
                    if (dist < 0f) continue;

                    pairs.Add(new DoorDistancePair {
                        doorA    = edge.fromNodeId,
                        doorB    = edge.toNodeId,
                        floor    = floor,
                        distance = dist
                    });
                }
            }

            Debug.Log($"[DoorDistMap] Piano {floor}: {pairs.Count} coppie adiacenti");
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