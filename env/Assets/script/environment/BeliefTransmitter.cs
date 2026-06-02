using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

// ============================================================
// BeliefTransmitter.cs
// ============================================================
// Trasmette lo snapshot dell'edificio a JaCaMo come credenze.
//
// FORMATO CREDENZE (compatibile con Jason .asl):
//   node(ID, Type, Floor, X, Y, Z)                  ← nodi stanza/corridoio
//   connector_link(ID, FloorA, FloorB, ...)          ← nodi scala/connettore (una sola volta)
//   edge(ID, From, To, Floor, EdgeType, DoorState, ...)
//   door_dist(DoorA, DoorB, Floor, Distance)
//   exploration_complete(...)
// ============================================================

public class BeliefTransmitter : AbstractMasElement
{
    [Header("Trasmissione")]
    public int beliefsPerFrame = 5;

    // --------------------------------------------------------
    // ENTRY POINT PRINCIPALE
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
    // WRAPPER DI COMPATIBILITÀ
    // --------------------------------------------------------
    public void TransmitGraph(
        Dictionary<int, TopologicalGraph> floorGraphs,
        List<FloorConnectorLink>          connectorLinks,
        List<RoomDistancePair>            roomDistances,
        string                            buildingId = "building")
    {
        var snapshot = GraphSnapshotBuilder.Build(
            floorGraphs, connectorLinks, roomDistances, buildingId);
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

        // ── Costruisci set dei connectorId che sono scale/connettori ────
        var connectorNodeIds = new HashSet<string>();
        foreach (var link in snapshot.connectorLinks)
            connectorNodeIds.Add(link.connectorId);

        // ── Tiene traccia dei connettori già trasmessi ───────
        var sentConnectors = new HashSet<string>();

        // ── 1. Nodi e archi per piano ────────────────────────
        foreach (var floor in snapshot.floors)
        {
            // Nodi
            foreach (var node in floor.nodes)
            {
                // Nodo scala/connettore: trasmetti come connector_link una sola volta
                if (connectorNodeIds.Contains(node.nodeId))
                {
                    if (sentConnectors.Contains(node.nodeId)) continue;
                    sentConnectors.Add(node.nodeId);

                    var link = snapshot.connectorLinks.Find(l => l.connectorId == node.nodeId);

                    SendBelief(new BeliefMessage
                    {
                        beliefType = "connector_link",
                        payload    = new Dictionary<string, object>
                        {
                            { "id",            node.nodeId        },
                            { "floorA",        link.floorA        },
                            { "floorB",        link.floorB        },
                            { "bidirectional", link.bidirectional  },
                            { "posAx",         link.posAx         },
                            { "posAy",         link.posAy         },
                            { "posAz",         link.posAz         },
                            { "posBx",         link.posBx         },
                            { "posBy",         link.posBy         },
                            { "posBz",         link.posBz         },
                        }
                    });

                    if (++count % beliefsPerFrame == 0) yield return null;
                    continue;
                }

                // Nodo normale (stanza o polo corridoio)
                SendBelief(new BeliefMessage
                {
                    beliefType = "node",
                    payload    = new Dictionary<string, object>
                    {
                        { "id",            node.nodeId           },
                        { "type",          node.nodeType         },
                        { "floor",         floor.floorIndex      },
                        { "x",             node.x                },
                        { "y",             node.y                },
                        { "z",             node.z                },
                        { "corridorId",    node.corridorId ?? "" },
                        { "poleLabel",     node.poleLabel  ?? "" },
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

        // ── 2. Distanze porte ────────────────────────────────
        foreach (var dist in snapshot.roomDistances)
        {
            SendBelief(new BeliefMessage
            {
                beliefType = "door_dist",
                payload    = new Dictionary<string, object>
                {
                    { "roomA",    dist.roomA    },
                    { "roomB",    dist.roomB    },
                    { "floor",    dist.floor    },
                    { "distance", dist.distance },
                }
            });

            if (++count % beliefsPerFrame == 0) yield return null;
        }

        // ── 3. Segnale di completamento ──────────────────────
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
                { "buildingId",  snapshot.buildingId          },
                { "capturedAt",  snapshot.capturedAt          },
                { "totalFloors", snapshot.floors.Count        },
                { "totalNodes",  totalNodes                   },
                { "totalEdges",  totalEdges                   },
                { "totalDists",  snapshot.roomDistances.Count },
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
// OGGETTI SCOPERTI A RUNTIME
// Chiamato da RoomObjectBridge quando il cono visivo vede
// un oggetto per la prima volta. Invia new_object all'agente
// Jason che crea dinamicamente RoomObjectArtifact e fa focus.
// --------------------------------------------------------
public void SendRoomObjectDiscovered(
    string artifactId, string roomId, int wsPort,
    float x, float y, float z)
{
    SendBelief(new BeliefMessage
    {
        beliefType = "new_object",
        payload    = new Dictionary<string, object>
        {
            { "artifactId", artifactId },
            { "roomId",     roomId     },
            { "wsPort",     wsPort     },
            { "x",          x          },
            { "y",          y          },
            { "z",          z          },
        }
    });

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