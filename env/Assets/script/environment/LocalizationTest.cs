using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class LocalizationTest : MonoBehaviour
{
    public VisionCone visionCone;   // il cono di QUESTO avatar (assegnare in inspector)

    [Header("Localizzazione tramite collider")]
    public LayerMask roomColliderMask;   // layer "RoomNode" (= roomNodeLayers del cono)
    public float searchRadius = 100f;    // raggio entro cui cercare i collider stanza

    private Dictionary<string, Vector3> seenRooms = new Dictionary<string, Vector3>();
    private string currentRoom = null;
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
    if (visionCone == null) return;

    GameObject go = ResolveRoomNode(roomName, position);
    if (go == null) { Debug.Log($"[Loc TEST] {roomName}: resolver NULL"); return; }
    if (!visionCone.IsInSight(go)) { Debug.Log($"[Loc TEST] {roomName}: scartata (non in sight dal MIO cono)"); return; }

    if (!seenRooms.ContainsKey(roomName))
    {
        seenRooms[roomName] = position;
    }
}

    // Recupera il GameObject del room node dalla sua posizione (l'evento dà solo nome+pos)
    private GameObject ResolveRoomNode(string roomName, Vector3 position)
    {
        Collider[] near = Physics.OverlapSphere(
            position, 0.2f, roomColliderMask, QueryTriggerInteraction.Collide);

        foreach (var c in near)
            if (c.gameObject.name == roomName)
                return c.gameObject;

        return null;
    }

    IEnumerator LocalizeOnce()
    {
        yield return new WaitForSeconds(1f);

        // 360° per far entrare nel cono tutte le stanze intorno (il cono è direzionale)
        float rotated = 0f;
        float speed = 90f;
        while (rotated < 360f)
        {
            float step = speed * Time.deltaTime;
            transform.Rotate(0, step, 0);
            rotated += step;
            yield return null;
        }

        // aspetta che almeno una stanza venga vista (e validata dal mio cono)
        float timeout = 5f;
        while (seenRooms.Count == 0 && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        if (seenRooms.Count == 0)
        {
            Debug.LogWarning("[Localizer] Nessuna stanza vista! Controlla VisionCone / roomColliderMask / occlusionLayers.");
            yield break;
        }

        currentRoom = NearestVisibleRoomByCollider();

        if (currentRoom == null)
        {
            Debug.LogWarning("[Localizer] Stanze viste ma nessun collider corrispondente nel raggio.");
            yield break;
        }
    
        Debug.Log($"[Localizer] Sono in: {currentRoom}");
        StartCoroutine(ResendCurrentRoom());
    }

    // Tra le sole stanze VISTE dal mio cono, sceglie la più vicina per distanza sul collider
    private string NearestVisibleRoomByCollider()
    {
        Collider[] cols = Physics.OverlapSphere(
            transform.position, searchRadius, roomColliderMask, QueryTriggerInteraction.Collide);

        string best = null;
        float minDist = float.MaxValue;

        foreach (var col in cols)
        {
            // scarta i collider di stanze che il mio cono NON ha visto (muri in mezzo ecc.)
            if (!seenRooms.ContainsKey(col.gameObject.name)) continue;

            // ClosestPoint == posizione -> dist 0 -> sono DENTRO la stanza
            Vector3 cp = col.ClosestPoint(transform.position);
            float d = Vector3.Distance(transform.position, cp);

            if (d < minDist)
            {
                minDist = d;
                best = col.gameObject.name;   // id stanza = nome GameObject (es. "Ufficio2")
                if (d == 0f) break;           // dentro la stanza: vince subito
            }
        }

        // fallback: se il collider non è nel raggio, usa la posizione del nodo vista dal cono
        if (best == null)
        {
            foreach (var kv in seenRooms)
            {
                float d = Vector3.Distance(transform.position, kv.Value);
                if (d < minDist) { minDist = d; best = kv.Key; }
            }
        }

        return best;
    }

    private IEnumerator ResendCurrentRoom()
    {
        string lastSent = null;
        float elapsed = 0f;
        float warmupDuration = 9f;

        while (true)
        {
            string roomToSend = _beliefTransmitter?.LastArrivedNode ?? currentRoom;

            if (roomToSend == null)
            {
                yield return new WaitForSeconds(3f);
                elapsed += 3f;
                continue;
            }

            bool changed = roomToSend != lastSent;
            bool inWarmup = elapsed < warmupDuration;

            if (changed || inWarmup)
            {
                _beliefTransmitter?.SendCurrentRoom(roomToSend);
                lastSent = roomToSend;
            }

            yield return new WaitForSeconds(3f);
            elapsed += 3f;
        }
    }
}