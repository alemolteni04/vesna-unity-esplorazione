using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

// ============================================================
// BeliefTransmitter.cs
// ============================================================
// Trasmette lo snapshot dell'edificio a JaCaMo come credenze.
//
// MODIFICA ARCHITETTURALE:
//   Il metodo principale è ora TransmitSnapshot(BuildingSnapshot).
//   Non dipende più dal grafo vivo: può trasmettere dati che
//   vengono dalla memoria, dal disco, o da qualsiasi altra fonte
//   che produca un BuildingSnapshot.
//
//   TransmitGraph() è mantenuto come wrapper di compatibilità
//   per i chiamanti che hanno ancora il grafo vivo in mano
//   (es. OnAllFloorsCompleted in ExplorationManager). Internamente
//   chiama GraphSnapshotBuilder.Build() + TransmitSnapshot().
//
// FORMATO CREDENZE (compatibile con Jason .asl):
//   node(ID, Type, X, Y, Z)
//   edge(FromID, ToID, EdgeID, EdgeType, DoorState)
//   connector_link(ConnectorID, FloorFrom, NodeFrom, FloorTo, NodeTo)
//   door_dist(DoorA, DoorB, Floor, Distance)
//   exploration_complete(TotalNodes, TotalEdges)
// ============================================================

public class BeliefTransmitter : AbstractMasElement
{
    [Header("Trasmissione")]
    public int beliefsPerFrame = 5;

    // --------------------------------------------------------
    // ENTRY POINT PRINCIPALE — accetta uno snapshot già pronto
    // Usato sia da ExplorationManager (dopo esplorazione live)
    // sia da ExplorationManager.Start() (caricamento da disco).
    // --------------------------------------------------------
    public void TransmitSnapshot(BuildingSnapshot snapshot)
    {
        if (snapshot == null)
        {
            Debug.LogError("[BeliefTransmitter] TransmitSnapshot: snapshot null.");
            return;
        }

        StartCoroutine(TransmitSnapshotCoroutine(snapshot));
    }

    // --------------------------------------------------------
    // WRAPPER DI COMPATIBILITÀ — mantiene l'API precedente
    // Converte il grafo vivo in snapshot e lo trasmette.
    // --------------------------------------------------------
    public void TransmitGraph(
        Dictionary<int, TopologicalGraph> floorGraphs,
        List<FloorConnectorLink>          connectorLinks,
        List<DoorDistancePair>            doorDistances,
        string                            buildingId = "building")
    {
        var snapshot = GraphSnapshotBuilder.Build(
            floorGraphs, connectorLinks, doorDistances, buildingId);

        TransmitSnapshot(snapshot);
    }

    // --------------------------------------------------------
    // COROUTINE PRINCIPALE
    // --------------------------------------------------------
    private IEnumerator TransmitSnapshotCoroutine(BuildingSnapshot snapshot)
    {
        Debug.Log($"[BeliefTransmitter] Inizio trasmissione snapshot '{snapshot.buildingId}' " +
                  $"({snapshot.floors.Count} piani) a JaCaMo...");

        int count = 0;

        // ── 1. Nodi e archi per piano ────────────────────────
        foreach (var floor in snapshot.floors)
        {
            // Nodi
            foreach (var node in floor.nodes)
            {
                SendBelief(new BeliefMessage
                {
                    beliefType = "node",
                    payload    = new Dictionary<string, object>
                    {
                        { "id",               node.nodeId           },
                        { "type",             node.nodeType         },
                        { "floor",            floor.floorIndex      },
                        { "x",                node.x                },
                        { "y",                node.y                },
                        { "z",                node.z                },
                        { "fullyExplored",    node.fullyExplored    },
                        { "corridorId",       node.corridorId ?? "" },
                        { "poleLabel",        node.poleLabel  ?? "" },
                    }
                });

                if (++count % beliefsPerFrame == 0) yield return null;
            }

            // Archi
            foreach (var edge in floor.edges)
            {
                SendBelief(new BeliefMessage
                {
                    beliefType = "edge",
                    payload    = new Dictionary<string, object>
                    {
                        { "id",              edge.edgeId           },
                        { "from",            edge.fromNodeId       },
                        { "to",              edge.toNodeId         },
                        { "floor",           floor.floorIndex      },
                        { "edgeType",        edge.edgeType         },
                        { "edgeState",       edge.edgeState        },
                        { "doorState",       edge.doorState        },
                        { "isPhysical",      edge.isPhysical       },
                        { "isCorridorLink",  edge.isCorridorLink   },
                        { "side",            edge.side ?? ""       },
                        { "distFromA",       edge.distFromA        },
                        { "distFromB",       edge.distFromB        },
                        { "orderFW",         edge.orderFW          },
                        { "orderBW",         edge.orderBW          },
                        { "explorationOrder",edge.explorationOrder },
                        { "x",               edge.x                },
                        { "y",               edge.y                },
                        { "z",               edge.z                },
                    }
                });

                if (++count % beliefsPerFrame == 0) yield return null;
            }
        }

        // ── 2. Link cross-floor ──────────────────────────────
        foreach (var link in snapshot.connectorLinks)
        {
            SendBelief(new BeliefMessage
            {
                beliefType = "connector_link",
                payload    = new Dictionary<string, object>
                {
                    { "connectorId", link.connectorId },
                    { "floorFrom",   link.floorFrom   },
                    { "nodeIdFrom",  link.nodeIdFrom   },
                    { "floorTo",     link.floorTo      },
                    { "nodeIdTo",    link.nodeIdTo     },
                }
            });

            if (++count % beliefsPerFrame == 0) yield return null;
        }

        // ── 3. Distanze porte ────────────────────────────────
        foreach (var dist in snapshot.doorDistances)
        {
            SendBelief(new BeliefMessage
            {
                beliefType = "door_dist",
                payload    = new Dictionary<string, object>
                {
                    { "doorA",    dist.doorA    },
                    { "doorB",    dist.doorB    },
                    { "floor",    dist.floor    },
                    { "distance", dist.distance },
                }
            });

            if (++count % beliefsPerFrame == 0) yield return null;
        }

        // ── 4. Segnale di completamento ──────────────────────
        int totalNodes = 0, totalEdges = 0;
        foreach (var f in snapshot.floors)
        {
            totalNodes += f.nodes.Count;
            totalEdges += f.edges.Count;
        }

        SendBelief(new BeliefMessage
        {
            beliefType = "exploration_complete",
            payload    = new Dictionary<string, object>
            {
                { "buildingId",   snapshot.buildingId             },
                { "capturedAt",   snapshot.capturedAt             },
                { "totalFloors",  snapshot.floors.Count           },
                { "totalNodes",   totalNodes                      },
                { "totalEdges",   totalEdges                      },
                { "totalLinks",   snapshot.connectorLinks.Count   },
                { "totalDists",   snapshot.doorDistances.Count    },
            }
        });

        Debug.Log($"[BeliefTransmitter] Trasmissione completata: {count} credenze inviate.");
    }

    // --------------------------------------------------------
    // INVIO SINGOLO MESSAGGIO
    // --------------------------------------------------------
    private void SendBelief(BeliefMessage msg)
    {
        string json = JsonConvert.SerializeObject(msg);
        wsChannel?.sendMessage(
            UnityJacamoIntegrationUtil.CreateAndConvertJacamoMessageIntoJsonString(
                msg.beliefType, null, null, "explorer_agent", json
            )
        );
    }

    // --------------------------------------------------------
    // DTO interno
    // --------------------------------------------------------
    [System.Serializable]
    private class BeliefMessage
    {
        public string                     beliefType;
        public Dictionary<string, object> payload;
    }
}