using UnityEngine;
using UnityEngine.AI;

public class FloorTransition : MonoBehaviour
{
    [Header("Destinazione")]
    public Transform exitPoint;  // dove spawna l'agente dall'altra parte

    [Header("Cooldown (evita doppio trigger)")]
    public float cooldown = 2f;
    private float lastTeleportTime = -99f;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        // Evita che il trigger di uscita si riattivi subito dopo l'arrivo
        if (Time.time - lastTeleportTime < cooldown) return;

        NavMeshAgent agent = other.GetComponent<NavMeshAgent>();
        if (agent == null) return;

        Teleport(agent, other.transform);
    }

    private void Teleport(NavMeshAgent agent, Transform agentTransform)
{
    // 1. Disabilita e ferma
    agent.ResetPath();
    agent.enabled = false;

    // 2. Sposta il transform
    agentTransform.position = exitPoint.position;
    agentTransform.rotation = exitPoint.rotation;

    // 3. Riabilita
    agent.enabled = true;

    // 4. FORZA la posizione esatta sulla NavMesh ← questo è il fix
    agent.Warp(exitPoint.position);

    lastTeleportTime = Time.time;
}

    // Chiamato dall'altro trigger per "avvisare" che qualcuno sta arrivando
    public void NotifyIncoming()
    {
        lastTeleportTime = Time.time;
    }
}