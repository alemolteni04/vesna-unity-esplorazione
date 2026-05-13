using UnityEngine;
using UnityEngine.AI;

public class AgentNavController : MonoBehaviour
{
    private NavMeshAgent agent;
    private Vector3 savedDestination;
    private bool hasPendingDestination = false;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
    }

    void Update()
    {
        // Quando il NavMesh viene riattivato, riprendi la destinazione
        if (hasPendingDestination && agent.enabled && agent.isOnNavMesh)
        {
            agent.SetDestination(savedDestination);
            hasPendingDestination = false;
        }
    }

    public void SetDestination(Vector3 destination)
    {
        savedDestination = destination;
        hasPendingDestination = true;

        if (agent.enabled && agent.isOnNavMesh)
            agent.SetDestination(destination);
    }

    public void OnBeforeTeleport()
    {
        // Salva la destinazione attuale prima del teletrasporto
        if (agent.hasPath)
            savedDestination = agent.destination;

        hasPendingDestination = true;
    }
}