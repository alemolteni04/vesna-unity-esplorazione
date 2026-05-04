using UnityEngine;

// ============================================================
// VerticalConnectorScript.cs
// ============================================================
// Trigger fisico per scale o ascensori nella scena Unity.
// Va messo sul GameObject della scala/ascensore.
//
// Quando l'agente entra nel trigger:
//   → notifica ExplorationManager via evento statico
//   → ExplorationManager decide se usarlo (solo se il piano
//     corrente è completamente esplorato)
//
// SETUP NELL'INSPECTOR:
//   - connectorId : deve matchare l'id nel BuildingGraph
//   - connectorType: Stairs o Elevator
//   - targetFloor : piano di destinazione
//
// LAYER: "connector" — visibile al VisionCone
// ============================================================

public class VerticalConnectorScript : MonoBehaviour
{
    [Header("Configurazione")]
    public string connectorId;
    public VerticalConnector.ConnectorType connectorType;
    public int targetFloor;

    // Evento statico: ExplorationManager si abbona
    public static event System.Action<string, int, Vector3> OnConnectorReached;

    private bool triggered = false;

    void Awake()
    {
        var col = GetComponent<SphereCollider>();
        if (col == null) col = gameObject.AddComponent<SphereCollider>();
        col.radius    = 1.0f;
        col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (triggered) return;
        if (!other.CompareTag("Agent")) return;
        triggered = true;

        OnConnectorReached?.Invoke(connectorId, targetFloor, transform.position);
        Debug.Log($"[Connector] {connectorId} raggiunto → piano {targetFloor}");
    }

    public void ResetTrigger() => triggered = false;
}
