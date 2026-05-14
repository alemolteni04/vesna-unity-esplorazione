using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

// ============================================================
// BeliefTransmitter.cs
// ============================================================
// Trasmette il grafo topologico a JaCaMo come credenze (beliefs)
// DOPO che l'esplorazione Unity è completamente finita.
//
// FILOSOFIA:
// JaCaMo durante l'esplorazione è PASSIVO.
// Quando ExplorationManager chiama TransmitGraph(), questo script
// serializza il grafo e lo manda a JaCaMo nodo per nodo, arco per
// arco, in streaming incrementale.
//
// FORMATO CREDENZE (compatibile con Jason .asl):
// Ogni nodo diventa:   node(ID, Type, X, Y, Z)
// Ogni arco diventa:   edge(FromID, ToID, EdgeID, DoorType, DoorState)
// Fine trasmissione:   exploration_complete
//
// L'agente Jason le riceve come belief nel BeliefBase e può
// usarle per la pianificazione (es. "vai dalla stanza A alla B").
// ============================================================

public class BeliefTransmitter : AbstractMasElement
{
    [Header("Trasmissione")]
    // Quante credenze mandare per frame (throttling)
    // Evita di congestionare il WebSocket con 100 messaggi in un tick
    public int beliefsPerFrame = 5;

    // --------------------------------------------------------
    // ENTRY POINT — chiamato da ExplorationManager
    // --------------------------------------------------------
    public void TransmitGraph(TopologicalGraph graph)
    {
        StartCoroutine(TransmitCoroutine(graph));
    }

    
    private IEnumerator TransmitCoroutine(TopologicalGraph graph)
    {
        Debug.Log("[BeliefTransmitter] Inizio trasmissione grafo a JaCaMo...");

        int count = 0;

        // ----------------------------------------
        // 1. Trasmetti tutti i nodi
        // ----------------------------------------
        foreach (var node in graph.AllNodes())
        {
            var msg = new BeliefMessage
            {
                beliefType = "node",
                payload    = new Dictionary<string, object>
                {
                    { "id",   node.id                 },
                    { "type", node.type.ToString()    },
                    { "x",    node.position.x         },
                    { "y",    node.position.y         },
                    { "z",    node.position.z         },
                    { "fullyExplored", node.fullyExplored }
                }
            };

            SendBelief(msg);
            count++;

            // Yield ogni N messaggi per non bloccare il main thread
            if (count % beliefsPerFrame == 0)
                yield return null;
        }

        // ----------------------------------------
        // 2. Trasmetti tutti gli archi
        // ----------------------------------------
        foreach (var node in graph.AllNodes())
        {
            foreach (var edge in node.edges)
            {
                var msg = new BeliefMessage
                {
                    beliefType = "edge",
                    payload    = new Dictionary<string, object>
                    {
                        { "id",         edge.id                        },
                        { "from",       edge.fromNodeId                },
                        { "to",         edge.toNodeId ?? "unknown"     },
                        { "isPhysical", edge.isPhysical                },
                        { "doorState",  edge.doorState.ToString()      },
                        { "side",       edge.side                      },
                        { "distFromA",  edge.distFromA                 },
                        { "distFromB",  edge.distFromB                 },
                        { "orderFW",    edge.orderFW                   },
                        { "orderBW",    edge.orderBW                   },
                        { "x",          edge.position.x                },
                        { "y",          edge.position.y                },
                        { "z",          edge.position.z                }
                    }
                };

                SendBelief(msg);
                count++;

                if (count % beliefsPerFrame == 0)
                    yield return null;
            }
        }

        //-----------------------------
        //TRASMETTI PORTE
        //------------------------------
        

        // ----------------------------------------
        // 3. Segnala completamento
        // ----------------------------------------
        var doneMsg = new BeliefMessage
        {
            beliefType = "exploration_complete",
            payload    = new Dictionary<string, object>
            {
                { "totalNodes", CountNodes(graph) },
                { "totalEdges", CountEdges(graph) }
            }
        };
        SendBelief(doneMsg);

        Debug.Log($"[BeliefTransmitter] Trasmissione completata: {count} credenze inviate.");
    }

//SALVATAGGIO DISTANZE PORTE
    public void TransmitDoorDistances(List<DoorDistancePair> pairs)
{
    StartCoroutine(TransmitDoorDistancesCoroutine(pairs));
}

private IEnumerator TransmitDoorDistancesCoroutine(List<DoorDistancePair> pairs)
{
    Debug.Log($"[BeliefTransmitter] Invio {pairs.Count} distanze porte...");
    int count = 0;

    foreach (var p in pairs)
    {
        var msg = new BeliefMessage
        {
            beliefType = "door_dist",
            payload    = new Dictionary<string, object>
            {
                { "doorA",    p.doorA    },
                { "doorB",    p.doorB    },
                { "floor",    p.floor    },
                { "distance", p.distance }
            }
        };

        SendBelief(msg);
        count++;

        if (count % beliefsPerFrame == 0)
            yield return null;
    }

    Debug.Log($"[BeliefTransmitter] {count} distanze inviate.");
}
    private void SendBelief(BeliefMessage msg)
    {
        string json = JsonConvert.SerializeObject(msg);
        wsChannel?.sendMessage(
            UnityJacamoIntegrationUtil.CreateAndConvertJacamoMessageIntoJsonString(
                msg.beliefType, null, null, "explorer_agent", json
            )
        );
    }

    private int CountNodes(TopologicalGraph g)
    {
        int n = 0;
        foreach (var _ in g.AllNodes()) n++;
        return n;
    }

    private int CountEdges(TopologicalGraph g)
    {
        int n = 0;
        foreach (var node in g.AllNodes()) n += node.edges.Count;
        return n;
    }

    // --------------------------------------------------------
    // Messaggio strutturato per JaCaMo
    // --------------------------------------------------------
    [System.Serializable]
    private class BeliefMessage
    {
        public string                       beliefType;
        public Dictionary<string, object>   payload;
    }
}
