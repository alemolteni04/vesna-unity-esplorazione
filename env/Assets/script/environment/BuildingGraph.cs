using System.Collections.Generic;
using UnityEngine;

// ============================================================
// BuildingGraph.cs — ScriptableObject
// ============================================================
// Il MACRO-GRAFO dell'edificio (Livello 0 della struttura).
// Viene creato UNA VOLTA nell'editor e assegnato all'
// ExplorationManager nell'Inspector.
//
// COME CREARLO:
//   Assets → Create → VEsNA → Building Graph
//   Poi nell'Inspector aggiungi i piani e i connettori verticali.
//
// COSA CONTIENE:
//   - Lista di piani (FloorNode): P0, P1, P2, ...
//   - Lista di connettori verticali (VerticalConnector): scale, ascensori
//   - Ogni connettore sa a quali piani è collegato e con che costo
//
// COSA NON CONTIENE:
//   - Le stanze e i corridoi di ogni piano (quelli li scopre Unity
//     durante l'esplorazione e li scrive nel TopologicalGraph)
//
// NOTA SULLA CONOSCENZA A PRIORI:
//   L'agente sa già che esistono P0, P1 e le scale prima di esplorare.
//   Questo è il "Macro-Grafo Statico" del documento.
//   I corridoi e le stanze invece vengono scoperti a runtime.
// ============================================================

[CreateAssetMenu(fileName = "BuildingGraph",
                 menuName  = "VEsNA/Building Graph")]
public class BuildingGraph : ScriptableObject
{
    [Header("Piani dell'edificio")]
    public List<FloorNode> floors = new List<FloorNode>();

    [Header("Connettori verticali (scale, ascensori)")]
    public List<VerticalConnector> connectors = new List<VerticalConnector>();

    // --------------------------------------------------------
    // API
    // --------------------------------------------------------

    // Restituisce il FloorNode del piano con l'indice dato
    public FloorNode GetFloor(int floorIndex)
    {
        return floors.Find(f => f.floorIndex == floorIndex);
    }

    // Trova tutti i connettori che collegano fromFloor a toFloor
    public List<VerticalConnector> GetConnectors(int fromFloor, int toFloor)
    {
        return connectors.FindAll(c =>
            (c.floorFrom == fromFloor && c.floorTo == toFloor) ||
            (c.bidirectional && c.floorFrom == toFloor && c.floorTo == fromFloor));
    }

    // Restituisce il piano successivo da esplorare dopo fromFloor.
    // Segue l'ordine della lista floors (non necessariamente numerico).
    public FloorNode GetNextFloor(int currentFloorIndex)
    {
        int currentPos = floors.FindIndex(f => f.floorIndex == currentFloorIndex);
        if (currentPos < 0 || currentPos >= floors.Count - 1) return null;
        return floors[currentPos + 1];
    }

    // Tutti i piani non ancora esplorati
    public List<FloorNode> GetUnexploredFloors()
    {
        return floors.FindAll(f => !f.explored);
    }
}

// ============================================================
// FLOOR NODE — un piano dell'edificio
// ============================================================
[System.Serializable]
public class FloorNode
{
    [Tooltip("Indice numerico del piano (0, 1, 2, ...)")]
    public int    floorIndex;

    [Tooltip("Nome leggibile (es. 'Piano Terra', 'Primo Piano')")]
    public string floorName;

    [Tooltip("Posizione di spawn dell'agente quando arriva a questo piano")]
    public Vector3 spawnPosition;

    // Aggiornato da ExplorationManager quando finisce il piano
    [HideInInspector] public bool explored = false;

    // Lista degli ID dei corridoi presenti in questo piano.
    // Viene popolata da ExplorationManager durante l'esplorazione
    // man mano che scopre corridoi (CorridorData.corridorId).
    // Serve per il check "ho esplorato tutto il piano?".
    [HideInInspector] public List<string> discoveredCorridorIds = new List<string>();
}

// ============================================================
// VERTICAL CONNECTOR — scala o ascensore
// ============================================================
[System.Serializable]
public class VerticalConnector
{
    public enum ConnectorType { Stairs, Elevator }

    [Tooltip("Tipo di connettore")]
    public ConnectorType type;

    [Tooltip("Nome/ID del connettore (es. 'stairs_A', 'elevator_1')")]
    public string id;

    [Tooltip("Piano di partenza")]
    public int floorFrom;

    [Tooltip("Piano di arrivo")]
    public int floorTo;

    [Tooltip("Se true, funziona in entrambe le direzioni")]
    public bool bidirectional = true;

    [Tooltip("Costo sforzo salita (usato da JaCaMo per pianificazione)")]
    public float costUp   = 2.0f;

    [Tooltip("Costo sforzo discesa")]
    public float costDown = 1.0f;

    // Posizione del trigger di INIZIO del connettore
    // (dove l'agente si avvicina per usarlo)
    public Vector3 triggerStartPosition;

    // Posizione di ARRIVO (dove l'agente compare dopo il cambio piano)
    public Vector3 triggerEndPosition;

    // Aggiornato a runtime dal VerticalConnectorScript
    [HideInInspector] public bool agentNearby = false;
}
