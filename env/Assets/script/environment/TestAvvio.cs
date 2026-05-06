using UnityEngine;

// ============================================================
// TestAvvio.cs
// ============================================================
// Avvia ExplorationManager dopo un breve delay.
// NON scrivere stringhe a mano per il nodo di partenza:
// il punto di partenza si configura trascinando il GameObject
// nel campo "startRoomObject" dell'ExplorationManager
// nell'Inspector. Questo script non fa altro che aspettare
// e invocare StartExploration() con i dati gia' configurati.
// ============================================================
public class TestAvvio : MonoBehaviour
{
    [Tooltip("Delay in secondi prima di avviare l'esplorazione")]
    public float startDelay = 1f;

    void Start()
    {
        Invoke(nameof(AvviaAgente), startDelay);
    }

    void AvviaAgente()
    {
        var manager = GetComponent<ExplorationManager>();
        if (manager == null)
        {
            UnityEngine.Debug.LogError("[TestAvvio] Nessun ExplorationManager su questo GameObject!");
            return;
        }
        if (manager.startRoomObject == null)
        {
            UnityEngine.Debug.LogError("[TestAvvio] startRoomObject non assegnato! " +
                           "Trascina il GO della stanza di partenza nel campo " +
                           "'Start Room Object' dell'Inspector di ExplorationManager.");
            return;
        }

        // Nome nodo e posizione letti dal GameObject trascinato — niente stringhe a mano.
        manager.StartExploration(
            manager.startRoomObject.name,
            manager.startRoomObject.transform.position
        );

        UnityEngine.Debug.Log($"[TestAvvio] Esplorazione avviata da '{manager.startRoomObject.name}'");
    }
}
