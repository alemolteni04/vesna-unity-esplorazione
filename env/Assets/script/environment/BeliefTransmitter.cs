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
    private readonly Queue<string> _pendingTargets = new Queue<string>();
    private readonly object _queueLock = new object();
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
    Dictionary<string, HashSet<string>> roomArtifacts = null, 
    string                            buildingId = "building")
{
    var snapshot = GraphSnapshotBuilder.Build(
        floorGraphs, connectorLinks, roomDistances, roomArtifacts, buildingId); 
    TransmitSnapshot(snapshot);
}

void Awake()
{
    if (!Application.IsPlaying(gameObject)) return;
    objInUse = gameObject;
    initializeWebSocketConnection(OnMessageFromJacamo);
    StartCoroutine(StartServerCoroutine());
}

private IEnumerator StartServerCoroutine()
{
    _ = startServer();
    yield return new WaitForSeconds(2f);
    Debug.Log($"[BeliefTransmitter] Server WebSocket pronto sulla porta {port}");
}
private void OnMessageFromJacamo(object sender, WebSocketSharp.MessageEventArgs e)
{
    string msg = e.Data;
    if (string.IsNullOrEmpty(msg)) return;

    try
    {
        var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(msg);
        if (json == null || !json.ContainsKey("type")) return;

        string type = json["type"].ToString();
        if (type != "walk") return;

        var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json["data"].ToString());
        string goType = data.ContainsKey("type") ? data["type"].ToString() : "";

       if (goType == "goto" && data.ContainsKey("target"))
        {
            string targetNode = data["target"].ToString();
            lock (_queueLock)
            {
                _pendingTargets.Enqueue(targetNode);
            }
        }
    }
    catch (System.Exception ex)
    {
        Debug.LogWarning($"[BeliefTransmitter] Messaggio non gestito ({ex.Message}): {msg}");
    }
}
void Update()
{
    string target = null;
    lock (_queueLock)
    {
        if (_pendingTargets.Count > 0)
            target = _pendingTargets.Dequeue();
    }
    if (target != null)
    {
        var mover = GetComponent<VesnaMover>();
        if (mover != null)
            mover.MoveTo(target);
        else
            Debug.LogWarning("[BeliefTransmitter] Ricevuto walk/goto ma VesnaMover non è presente.");
    }
}
// --------------------------------------------------------
// Notifica a JaCaMo che il movimento è completato.
// Manda un "signal" che VesnaAgent traduce in
// movement(completed, destination_reached).
// --------------------------------------------------------
public void SendMovementCompleted(string targetNode)
{
    string json = JsonConvert.SerializeObject(new Dictionary<string, object>
    {
        { "type", "movement" },
        { "status", "completed" },
        { "reason", "destination_reached" },
        { "name", targetNode }
    });

   wsChannel?.sendMessage(
    UnityJacamoIntegrationUtil.CreateAndConvertJacamoMessageIntoJsonString(
        "destinationReached", null, "reached_destination", "vesna_agent", targetNode
    )
);

    // Aggiorna anche current_room, così go_to/RCC sa dove è arrivata
    SendCurrentRoom(targetNode);
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
                        { "wsPort",        node.wsPort           },
                    }
                });

                if (++count % beliefsPerFrame == 0) yield return null;
            }
            // ── Credenze corridoio ───────────────────────────────
            var corridorsSent = new HashSet<string>();
          foreach (var floorSnap in snapshot.floors)
            {
                foreach (var node in floorSnap.nodes)
                {
                    if (node.nodeType != "CorridorPoleA") continue;
                    if (corridorsSent.Contains(node.corridorId)) continue;
                    var poleB = floorSnap.nodes.Find(n =>
                        n.corridorId == node.corridorId && n.nodeType == "CorridorPoleB");
                    if (poleB == null) continue;
                    corridorsSent.Add(node.corridorId);
                    SendBelief(new BeliefMessage
                    {
                        beliefType = "corridor",
                        payload = new Dictionary<string, object>
                        {
                            { "corridorId", node.corridorId      },
                            { "poleA",      node.nodeId           },
                            { "poleB",      poleB.nodeId          },
                            { "floor",      floorSnap.floorIndex  },
                            { "wsPort",     node.wsPort           },
                        }
                    });
                    if (++count % beliefsPerFrame == 0) yield return null;
                }
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
                        { "wsPort",          edge.wsPort           },
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
    private void SendBeliefTo(BeliefMessage msg, string receiver)
{
    string json = JsonConvert.SerializeObject(msg);
    wsChannel?.sendMessage(
        UnityJacamoIntegrationUtil.CreateAndConvertJacamoMessageIntoJsonString(
            msg.beliefType, null, null, receiver, json
        )
    );
}

private void SendBelief(BeliefMessage msg) => SendBeliefTo(msg, "explorer_agent");
    // --------------------------------------------------------
// OGGETTI SCOPERTI A RUNTIME
// Chiamato da RoomObjectBridge quando il cono visivo vede
// un oggetto per la prima volta. Invia new_object all'agente
// Jason che crea dinamicamente RoomObjectArtifact e fa focus.
// --------------------------------------------------------
public void SendRoomObjectDiscovered(
    string artifactId, string roomId, string artifactType, int wsPort,
    float x, float y, float z)
{
    SendBelief(new BeliefMessage
    {
        beliefType = "new_object",
        payload    = new Dictionary<string, object>
        {
            { "artifactId",   artifactId   },
            { "roomId",       roomId       },
            { "artifactType", artifactType },
            { "wsPort",       wsPort       },
            { "x",            x            },
            { "y",            y            },
            { "z",            z            },
        }
    });
}

public void SendCurrentRoom(string roomId)
{
    SendBeliefTo(new BeliefMessage
    {
        beliefType = "current_room",
        payload    = new Dictionary<string, object>
        {
            { "roomId", roomId }
        }
    }, "vesna_agent");
    Debug.Log($"[BeliefTransmitter] Stanza corrente inviata: {roomId}");
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