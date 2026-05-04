using UnityEngine;

public class TestAvvio : MonoBehaviour
{
    void Start()
    {
        // Aspetta 1 secondo dopo aver premuto Play, poi avvia l'agente
        Invoke("AvviaAgente", 1f); 
    }

    void AvviaAgente()
    {
        ExplorationManager manager = GetComponent<ExplorationManager>();
        if (manager != null)
        {
            // Avvia l'esplorazione, dicendo all'agente da dove parte
            manager.StartExploration("punto_di_partenza", transform.position);
            Debug.Log("Comando di avvio inviato all'agente!");
        }
        else
        {
            Debug.LogError("Non trovo l'ExplorationManager su questo oggetto!");
        }
    }
}