using UnityEngine;

// ============================================================
// CorridorData.cs
// ============================================================
// Componente sul GameObject padre del corridoio.
// Sostituisce CorridorManager che era troppo pesante.
//
// RESPONSABILITÀ UNICA: essere un contenitore di dati fisici.
// Non gestisce WebSocket, non gestisce movimento, non gestisce
// stati algoritmici. Solo dati.
//
// COSA FA:
//   1. Tiene l'ID univoco del corridoio
//   2. All'avvio trova i figli Pole e Door e li configura
//   3. Espone le posizioni dei poli a ExplorationManager
//
// COSA NON FA (a differenza del vecchio CorridorManager):
//   - Non apre WebSocket
//   - Non manda init_poles a JaCaMo
//   - Non gestisce goto_pole
//   - Non sa niente dell'algoritmo di esplorazione
//
// L'ID viene auto-assegnato da CorridorAutoSetup.cs al drag-drop
// e può essere rinominato nell'Inspector.
// ============================================================

public class CorridorData : MonoBehaviour
{
    [Header("Identificazione")]
    public string corridorId = "corridor_1";

    // Lette all'Awake dai figli — sola lettura per gli esterni
    public Vector3 Pole1Position { get; private set; }
    public Vector3 Pole2Position { get; private set; }

    // Riferimenti ai poli — usati da ExplorationManager
    public CorridorPoleScript Pole1 { get; private set; }
    public CorridorPoleScript Pole2 { get; private set; }

    void Awake()
    {
        if (!Application.IsPlaying(gameObject)) return;
        ConfigureChildren();
    }

    private void ConfigureChildren()
    {
        var poles = GetComponentsInChildren<CorridorPoleScript>();
        if (poles.Length != 2)
        {
            Debug.LogError($"[CorridorData] {corridorId}: servono 2 poli, trovati {poles.Length}");
            return;
        }

        Pole1 = poles[0];
        Pole2 = poles[1];

        Pole1.corridorId = corridorId;
        Pole1.poleId     = "1";
        Pole2.corridorId = corridorId;
        Pole2.poleId     = "2";

        Pole1Position = Pole1.transform.position;
        Pole2Position = Pole2.transform.position;

        

        Debug.Log($"[CorridorData] {corridorId} configurato: P1={Pole1Position} P2={Pole2Position}");
    }

    // Rinomina il corridoio e aggiorna i figli — chiamato dall'editor
    public void OnCorridorIdChanged()
    {
        gameObject.name = corridorId;
        var poles = GetComponentsInChildren<CorridorPoleScript>();
        if (poles.Length > 0) { poles[0].gameObject.name = $"Pole_1_{corridorId}"; poles[0].corridorId = corridorId; }
        if (poles.Length > 1) { poles[1].gameObject.name = $"Pole_2_{corridorId}"; poles[1].corridorId = corridorId; }
    }
}
