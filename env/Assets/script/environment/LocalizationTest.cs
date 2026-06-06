using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class LocalizationTest : MonoBehaviour
{
    public VisionCone visionCone;

    private Dictionary<string, Vector3> seenRooms = new Dictionary<string, Vector3>();
    private string currentRoom = null;
    public string CurrentRoom => currentRoom;

    void Start()
    {
        VisionCone.OnRoomNodeVisible += OnRoomSeen;
        StartCoroutine(LocalizeOnce());
    }

    void OnDestroy()
    {
        VisionCone.OnRoomNodeVisible -= OnRoomSeen;
    }

    private void OnRoomSeen(string roomName, Vector3 position)
    {
        if (!seenRooms.ContainsKey(roomName))
            seenRooms[roomName] = position;
    }

    IEnumerator LocalizeOnce()
    {
         // aspetta che il VisionCone abbia già scansionato qualcosa
        yield return new WaitForSeconds(1f);
        // Fai il 360° per vedere le stanze intorno
        float rotated = 0f;
        float speed = 90f;
        while (rotated < 360f)
        {
            float step = speed * Time.deltaTime;
            transform.Rotate(0, step, 0);
            rotated += step;
            yield return null;
        }

        // Trova la stanza più vicina tra quelle viste
        float minDist = float.MaxValue;
        foreach (var kv in seenRooms)
        {
            float dist = Vector3.Distance(transform.position, kv.Value);
            if (dist < minDist)
            {
                minDist = dist;
                currentRoom = kv.Key;
            }
        }

        Debug.Log($"[Localizer] Sono in: {currentRoom}");
    }
}