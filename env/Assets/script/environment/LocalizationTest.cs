using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class LocalizationTest : MonoBehaviour
{
    public VisionCone visionCone;

    private Dictionary<string, Vector3> seenRooms = new Dictionary<string, Vector3>();
    private string currentRoom=null;
private BeliefTransmitter _beliefTransmitter;
    public string CurrentRoom => currentRoom;

    public void SetCurrentRoom(string roomId)
{
    currentRoom = roomId;
}

    void Start()
{
    VisionCone.OnRoomNodeVisible += OnRoomSeen;
    _beliefTransmitter = GetComponent<BeliefTransmitter>();
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

    // ← aggiungi: aspetta che almeno una stanza venga vista
    float timeout = 5f;
    while (seenRooms.Count == 0 && timeout > 0f)
    {
        timeout -= Time.deltaTime;
        yield return null;
    }

    if (seenRooms.Count == 0)
    {
        Debug.LogWarning("[Localizer] Nessuna stanza vista! Controlla roomNodeLayers.");
        yield break;
    }

    // Trova la stanza più vicina
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
    StartCoroutine(ResendCurrentRoom());
}
private IEnumerator ResendCurrentRoom()
{
    string lastSent = null;
    float elapsed = 0f;
    float warmupDuration = 60f;

    while (true)
    {
        bool changed = currentRoom != lastSent;
        bool inWarmup = elapsed < warmupDuration;

        if (changed || inWarmup)
        {
            _beliefTransmitter?.SendCurrentRoom(currentRoom);
            lastSent = currentRoom;
        }

        yield return new WaitForSeconds(2f);
        elapsed += 2f;
    }
}
}