using System.Collections.Generic;
using UnityEngine;

// ============================================================
// DoorSensor.cs
// ============================================================
// Montato sull'agente. Rileva porte/varchi (DoorVarcoScript)
// durante il transito A→B e li registra nel TopologicalGraph.
//
// ATTIVAZIONE: ExplorationManager chiama StartScan() quando
// l'agente tocca il primo polo. StopScan() al secondo polo.
//
// COSA CALCOLA per ogni porta rilevata:
//   - distFromA: distanza NavMesh dal polo A
//   - distFromB: distanza NavMesh dal polo B
//   - side: LEFT/RIGHT (prodotto vettoriale)
//   - orderFW: ordine di scoperta (1, 2, 3, ...)
//
// FILTRO DISTANZA: scarta porte oltre il polo B
// FILTRO VISIONE: usa VisionCone.IsInSight()
// ============================================================

public class DoorSensor : MonoBehaviour
{
    [Header("Riferimenti")]
    public VisionCone visionCone;

    [Header("Parametri")]
    public int scanFrequency = 10;

    private bool      isScanning   = false;
    private Vector3   poleAPos;
    private Vector3   poleBPos;
    private string    currentCorridorId;
    private int       fwCounter    = 1;
    private HashSet<string> seen   = new HashSet<string>();

    private float scanInterval;
    private float scanTimer;

    // Evento: nuova porta trovata durante il transito
    // Parametri: DoorVarcoScript, corridorId, orderFW
    public static event System.Action<DoorVarcoScript, string, int> OnDoorDiscovered;

    void Awake() => scanInterval = 1f / scanFrequency;

    void Update()
    {
        if (!isScanning) return;
        scanTimer -= Time.deltaTime;
        if (scanTimer > 0) return;
        scanTimer = scanInterval;
        Scan();
    }

    public void StartScan(Vector3 poleA, Vector3 poleB, string corridorId)
    {
        poleAPos          = poleA;
        poleBPos          = poleB;
        currentCorridorId = corridorId;
        isScanning        = true;
        fwCounter         = 1;
        seen.Clear();
        Debug.Log($"[DoorSensor] Scan avviato per {corridorId}");
    }

    public void StopScan()
    {
        isScanning = false;
        Debug.Log("[DoorSensor] Scan fermato.");
    }

    private void Scan()
    {
        float radius = visionCone != null ? visionCone.distance : 10f;

        Collider[] hits = Physics.OverlapSphere(
            transform.position, radius,
            LayerMask.GetMask("doors")
        );

        foreach (Collider col in hits)
        {
            var door = col.GetComponent<DoorVarcoScript>();
            if (door == null) continue;

            string name = door.gameObject.name;
            if (seen.Contains(name)) continue;

            // Filtro distanza: scarta porte oltre polo B
            float dAgentToB    = Vector3.Distance(transform.position, poleBPos);
            float dAgentToDoor = Vector3.Distance(transform.position, door.transform.position);
            if (dAgentToDoor > dAgentToB+0.3f) continue;

            // Filtro cono di visione
           // if (visionCone != null && !visionCone.IsInSight(door.gameObject)) continue;

            // Calcola geometria
            door.distFromA = door.NavMeshDistanceTo(poleAPos);
            door.distFromB = door.NavMeshDistanceTo(poleBPos);
            door.orderFW   = fwCounter;
            door.discovered = true;

            // Lato: prodotto vettoriale direzione agente × direzione porta
            Vector3 toDoor = (door.transform.position - transform.position).normalized;
            Vector3 cross  = Vector3.Cross(transform.forward, toDoor);
            door.side = cross.y >= 0 ? "RIGHT" : "LEFT";

            seen.Add(name);

            // Notifica ExplorationManager
            OnDoorDiscovered?.Invoke(door, currentCorridorId, fwCounter);
            fwCounter++;

            Debug.Log($"[DoorSensor] Porta scoperta: {name} dA={door.distFromA:F1} " +
                      $"dB={door.distFromB:F1} side={door.side} fw={door.orderFW}");
        }
    }
}
