using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// VisionCone.cs
// ============================================================
// Sensore visivo dell'agente. Figlio del GameObject avatar.
//
// COSA RILEVA durante l'esplorazione:
//   - Layer "doors"    → DoorVarcoScript  (porte e varchi)
//   - Layer "corridor" → CorridorPoleScript (poli dei corridoi)
//
// COSA NON FA (rimosso dalla versione originale):
//   - Niente logica "shops"
//   - Non ferma l'agente quando vede qualcosa
//   - Non ha più foundArtifacts generici
//
// MODALITÀ:
//   explorationMode = true  → rileva corridoi e porte
//   explorationMode = false → sensore disattivato (pianificazione)
//
// EVENTI STATICI (ExplorationManager si abbona):
//   OnCorridorPoleVisible(corridorId, poleId, position)
//     → l'agente vede un polo di corridoio non ancora visitato
//   OnDoorVisibleInRoom(DoorVarcoScript)
//     → l'agente vede una porta/varco durante la rotazione 360°
//
// LAYER SETUP (da configurare in Unity):
//   "doors"    → tutti i prefab DoorVarcoScript
//   "corridor" → tutti i prefab CorridorPoleScript
// ============================================================

[ExecuteInEditMode]
public class VisionCone : MonoBehaviour
{
    // ── Parametri del cono ────────────────────────────────────────────────────
    [Header("Parametri cono")]
    public float distance      = 10f;
    public float angle         = 30f;   // semi-angolo
    public float height        = 2.0f;
    public Color meshColor     = Color.red;

    [Header("Layer")]
    public LayerMask doorLayers;        // layer "doors" — DoorVarcoScript
    public LayerMask corridorLayers;    // layer "corridor" — CorridorPoleScript
    public LayerMask occlusionLayers;   // muri che bloccano la vista

    [Header("Scan")]
    public int scanFrequency = 30;

    [Header("Modalità")]
    public bool explorationMode = false;

    // ── Stato interno ─────────────────────────────────────────────────────────
    private GameObject   avatar;
    private NavMeshAgent agent;
    private float        scanInterval;
    private float        scanTimer;
    private Collider[]   buffer = new Collider[50];
    private int          count;

    private ExplorationManager explorationManager;

    // Tiene traccia dei poli già segnalati per evitare eventi ripetuti
    private HashSet<string> reportedPoles = new HashSet<string>();

    // ── Mesh del cono (solo per Gizmo) ────────────────────────────────────────
    private Mesh mesh;

    // ── EVENTI STATICI ────────────────────────────────────────────────────────

    /// <summary>
    /// Fired quando l'agente vede un polo di corridoio non ancora visitato.
    /// ExplorationManager si abbona per avvicinarsi al polo.
    /// </summary>
    public static event System.Action<string, string, Vector3> OnCorridorPoleVisible;

    /// <summary>
    /// Fired durante la rotazione 360° in una stanza quando viene vista
    /// una porta o varco non ancora registrata.
    /// ExplorationManager si abbona per aggiungere l'arco al grafo.
    /// </summary>
    public static event System.Action<DoorVarcoScript> OnDoorVisibleInRoom;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Start()
{
    avatar = transform.parent.gameObject;
    agent = avatar.GetComponent<NavMeshAgent>();
    // Recupera il riferimento al manager
    explorationManager = avatar.GetComponent<ExplorationManager>(); 
    scanInterval = 1f / Mathf.Max(1, scanFrequency);
}

    void Update()
    {
        if (!Application.IsPlaying(gameObject)) return;
        if (!explorationMode) return;

        scanTimer -= Time.deltaTime;
        if (scanTimer < 0f)
        {
            scanTimer += scanInterval;
            Scan();
        }
    }

    // ── Attivazione/Disattivazione ────────────────────────────────────────────

    /// <summary>
    /// Chiamato da ExplorationManager per abilitare/disabilitare il sensore.
    /// Durante la pianificazione JaCaMo il cono è spento.
    /// </summary>
    public void SetExplorationMode(bool active)
    {
        explorationMode = active;
        if (!active) reportedPoles.Clear();
        Debug.Log($"[VisionCone] explorationMode = {active}");
    }

    /// <summary>
    /// Resetta la memoria dei poli segnalati.
    /// Chiamato da ExplorationManager quando inizia un nuovo corridoio.
    /// </summary>
    public void ResetPoleMemory() => reportedPoles.Clear();

    // ── Scan ──────────────────────────────────────────────────────────────────

   private void Scan()
{

    ScanForCorridorPoles();
    
    if (explorationManager != null && explorationManager.inRoomMode)
    {
        ScanForDoorsInRoom();
    }
}
    /// <summary>
    /// Cerca poli di corridoio visibili.
    /// Fired solo se il polo non è stato già segnalato.
    /// </summary>
    private void ScanForCorridorPoles()
    {
        count = Physics.OverlapSphereNonAlloc(
            transform.position, distance, buffer,
            corridorLayers, QueryTriggerInteraction.Collide);

        for (int i = 0; i < count; i++)
        {
            var go   = buffer[i].gameObject;
            var pole = go.GetComponent<CorridorPoleScript>();
            if (pole == null) continue;

            string key = $"{pole.corridorId}_{pole.poleId}";
            if (reportedPoles.Contains(key)) continue;
            if (!IsInSight(go)) continue;

            reportedPoles.Add(key);
            Debug.Log($"[VisionCone] Polo visibile: {key}");
            OnCorridorPoleVisible?.Invoke(pole.corridorId, pole.poleId, go.transform.position);
        }
    }

    /// <summary>
    /// Cerca porte/varchi visibili — usato durante la rotazione 360°.
    /// Non segnala porte già marcate come discovered.
    /// </summary>
    private void ScanForDoorsInRoom()
    {
        int doorCount = Physics.OverlapSphereNonAlloc(
            transform.position, distance, buffer,
            doorLayers, QueryTriggerInteraction.Collide);

        for (int i = 0; i < doorCount; i++)
        {
            var go   = buffer[i].gameObject;
            var door = go.GetComponent<DoorVarcoScript>();
            if (door == null) continue;
            if (door.discovered) continue;      // già nel grafo
            if (!IsInSight(go)) continue;

            Debug.Log($"[VisionCone] Porta/Varco visibile in stanza: {go.name}");
            OnDoorVisibleInRoom?.Invoke(door);
        }
    }

    // ── IsInSight — invariato rispetto all'originale ──────────────────────────

    /// <summary>
    /// Controlla se un GameObject è dentro il cono di visione.
    /// Considera altezza, angolo e occlusione da muri.
    /// </summary>
  public bool IsInSight(GameObject targetObj)
{
    Vector3 origin = transform.position;
    origin.y += height / 2f; // Altezza occhi
    
    Vector3 dest = targetObj.transform.position;
    dest.y = origin.y; 

    Vector3 direction = dest - origin;
    float dist = direction.magnitude;

    // 1. Controllo Angolo
    if (Vector3.Angle(direction, transform.forward) > angle) return false;

    // 2. Raycast SINGOLO (non All)
    // Usiamo una maschera che include Muri (occlusionLayers) e Porte (doorLayers)
    RaycastHit hit;
    if (Physics.Raycast(origin, direction.normalized, out hit, dist, occlusionLayers | doorLayers))
    {
        // Se colpiamo qualcosa che NON è il nostro obiettivo...
        if (hit.collider.gameObject != targetObj)
        {
            // ...e questo qualcosa NON è un polo (i poli li consideriamo trasparenti per vedere oltre)
            if (hit.collider.GetComponentInParent<CorridorPoleScript>() == null)
            {
                // Allora la vista è coperta da un muro o da un altro varco
                return false; 
            }
        }
    }
    return true;
}
    // ── Gizmo ─────────────────────────────────────────────────────────────────

    private void OnValidate()
    {
        mesh         = CreateWedgeMesh();
        scanInterval = 1f / Mathf.Max(1, scanFrequency);
    }

    private void OnDrawGizmos()
    {
        if (mesh)
        {
            Gizmos.color = meshColor;
            Gizmos.DrawMesh(mesh, transform.position, transform.rotation);
        }

        Gizmos.DrawWireSphere(transform.position, distance);
    }

    // ── Mesh a cuneo (identica all'originale) ─────────────────────────────────

    private Mesh CreateWedgeMesh()
    {
        Mesh m = new Mesh();

        int segments     = 10;
        int numTriangles = (segments * 4) + 2 + 2;
        int numVertices  = numTriangles * 3;

        Vector3[] vertices  = new Vector3[numVertices];
        int[]     triangles = new int[numVertices];

        Vector3 bottomCenter = Vector3.zero;
        Vector3 bottomLeft   = Quaternion.Euler(0, -angle, 0) * Vector3.forward * distance;
        Vector3 bottomRight  = Quaternion.Euler(0,  angle, 0) * Vector3.forward * distance;
        Vector3 topCenter    = bottomCenter + Vector3.up * height;
        Vector3 topLeft      = bottomLeft   + Vector3.up * height;
        Vector3 topRight     = bottomRight  + Vector3.up * height;

        int vert = 0;

        // Lato sinistro
        vertices[vert++] = bottomCenter; vertices[vert++] = bottomLeft;  vertices[vert++] = topLeft;
        vertices[vert++] = topLeft;      vertices[vert++] = topCenter;   vertices[vert++] = bottomCenter;

        // Lato destro
        vertices[vert++] = bottomCenter; vertices[vert++] = topCenter;   vertices[vert++] = topRight;
        vertices[vert++] = topRight;     vertices[vert++] = bottomRight; vertices[vert++] = bottomCenter;

        float currentAngle = -angle;
        float deltaAngle   = (angle * 2f) / segments;

        for (int i = 0; i < segments; i++)
        {
            bottomLeft  = Quaternion.Euler(0, currentAngle,             0) * Vector3.forward * distance;
            bottomRight = Quaternion.Euler(0, currentAngle + deltaAngle, 0) * Vector3.forward * distance;
            topLeft     = bottomLeft  + Vector3.up * height;
            topRight    = bottomRight + Vector3.up * height;

            // Faccia lontana
            vertices[vert++] = bottomLeft;  vertices[vert++] = bottomRight; vertices[vert++] = topRight;
            vertices[vert++] = topRight;    vertices[vert++] = topLeft;     vertices[vert++] = bottomLeft;

            // Tetto
            vertices[vert++] = topCenter; vertices[vert++] = topLeft;    vertices[vert++] = topRight;

            // Pavimento
            vertices[vert++] = bottomCenter; vertices[vert++] = bottomLeft; vertices[vert++] = bottomRight;

            currentAngle += deltaAngle;
        }

        for (int i = 0; i < numVertices; i++) triangles[i] = i;

        m.vertices  = vertices;
        m.triangles = triangles;
        m.RecalculateNormals();
        return m;
    }
}
