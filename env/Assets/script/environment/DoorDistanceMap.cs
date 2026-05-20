using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;


// ============================================================
// DoorDistanceMap.cs
// ============================================================
// Calcola e memorizza le distanze NavMesh tra tutte le porte
// dell'edificio, piano per piano.
//
// COME FUNZIONA:
//   Dopo che ExplorationManager ha completato l'esplorazione,
//   chiama Compute(allGraphs). Il metodo scorre tutti i grafi
//   topologici (uno per piano), estrae le posizioni delle porte
//   dagli archi di tipo DoorFW, DoorBW e RoomDoor, e calcola
//   la distanza NavMesh reale tra ogni coppia di porte.
//   I risultati vengono salvati nella lista pubblica `pairs`,
//   visibile nell'Inspector di Unity.
//
// OUTPUT:
//   - `pairs`: lista di DoorDistancePair (doorA, doorB, floor,
//     distance) consultabile da altri script in Unity.
//   - Le distanze vengono anche inviate a JaCaMo come credenze
//     tramite BeliefTransmitter (beliefType = "door_dist"),
//     così l'agente può usarle per pianificare i percorsi.
//
// DIPENDENZE:
//   - TopologicalGraph.cs  (grafi per piano da ExplorationManager)
//   - NavMesh Unity        (deve essere baked nella scena)
//   - BeliefTransmitter.cs (per la trasmissione a JaCaMo)
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

            // Raccogli le posizioni di tutte le porte dagli archi
            var doorPositions = new Dictionary<string, Vector3>();

            foreach (var node in graph.AllNodes())
            {
                foreach (var edge in node.edges)
                {
                    if (edge == null) continue;
                    if (edge.edgeType == EdgeType.DoorFW  ||
                        edge.edgeType == EdgeType.DoorBW  ||
                        edge.edgeType == EdgeType.Door)
                    {
                        if (!string.IsNullOrEmpty(edge.id) &&
                            !doorPositions.ContainsKey(edge.id))
                        {
                            doorPositions[edge.id] = edge.position;
                        }
                    }
                }
            }

            var doorList = new List<string>(doorPositions.Keys);
            Debug.Log($"[DoorDistMap] Piano {floor} — porte: " + string.Join(", ", doorList));

            for (int i = 0; i < doorList.Count; i++)
            {
                for (int j = i + 1; j < doorList.Count; j++)
                {
                    float dist = NavMeshDistance(
                        doorPositions[doorList[i]],
                        doorPositions[doorList[j]]);

                    if (dist < 0f) continue;

                    pairs.Add(new DoorDistancePair {
                        doorA    = doorList[i],
                        doorB    = doorList[j],
                        floor    = floor,
                        distance = dist
                    });
                }
            }

            Debug.Log($"[DoorDistMap] Piano {floor}: {doorList.Count} porte, {pairs.Count} coppie");
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