using UnityEngine;
using UnityEngine.AI;
using System.Collections;
using System.Collections.Generic;

public class TestAgentWalker : MonoBehaviour
{
    public ExplorationManager explorationManager;
    public VisionCone visionCone;
    
    private NavMeshAgent agent;
    private HashSet<string> visitedRooms = new HashSet<string>();
    private bool isRotating = false;
    private bool isMoving = false;
    private string currentTarget = null;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        
        // Ascolta l'evento del VisionCone
        VisionCone.OnRoomNodeVisible += OnRoomSeen;
        
        StartCoroutine(WaitForGraphThenStart());
    }

    void OnDestroy()
    {
        VisionCone.OnRoomNodeVisible -= OnRoomSeen;
    }

    IEnumerator WaitForGraphThenStart()
    {
        // Aspetta che lo snapshot sia caricato
        yield return new WaitForSeconds(2f);
        
        Debug.Log("[TestWalker] Parto con il 360°");
        StartCoroutine(ExploreLoop());
    }

    IEnumerator ExploreLoop()
    {
        while (true)
        {
            // 1. Fai un 360° per vedere le stanze intorno
            yield return StartCoroutine(Rotate360());
            
            // 2. Trova la stanza più vicina non ancora visitata
            string nearest = FindNearestUnvisitedRoom();
            
            if (nearest == null)
            {
                Debug.Log("[TestWalker] Tutte le stanze visitate!");
                yield break;
            }
            
            // 3. Vai alla stanza
            Debug.Log($"[TestWalker] Vado a: {nearest}");
            yield return StartCoroutine(GoToRoom(nearest));
            
            // 4. Segna come visitata
            visitedRooms.Add(nearest);
            Debug.Log($"[TestWalker] Arrivato a: {nearest}");
        }
    }

    IEnumerator Rotate360()
    {
        isRotating = true;
        float rotated = 0f;
        float speed = 90f; // gradi al secondo

        Debug.Log("[TestWalker] Eseguo 360°...");
        
        while (rotated < 360f)
        {
            float step = speed * Time.deltaTime;
            transform.Rotate(0, step, 0);
            rotated += step;
            yield return null;
        }
        
        isRotating = false;
        Debug.Log("[TestWalker] 360° completato");
    }

    // Stanze viste durante il 360°
    private Dictionary<string, Vector3> seenRooms = new Dictionary<string, Vector3>();

    private void OnRoomSeen(string roomName, Vector3 position)
    {
        if (!seenRooms.ContainsKey(roomName))
        {
            seenRooms[roomName] = position;
            Debug.Log($"[TestWalker] Vedo la stanza: {roomName}");
        }
    }

    private string FindNearestUnvisitedRoom()
    {
        string nearest = null;
        float minDist = float.MaxValue;

        foreach (var kv in seenRooms)
        {
            if (visitedRooms.Contains(kv.Key)) continue;

            float dist = Vector3.Distance(transform.position, kv.Value);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = kv.Key;
            }
        }

        return nearest;
    }

    IEnumerator GoToRoom(string roomName)
    {
        Vector3 destination = seenRooms[roomName];
        agent.SetDestination(destination);
        isMoving = true;

        while (true)
        {
            if (!agent.pathPending && agent.remainingDistance <= 1f)
                break;
            yield return null;
        }

        isMoving = false;
    }
}