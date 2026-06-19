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
    private readonly Queue<string> _pendingTargets = new Queue<string>(); //coda dei comandi di movimento che arrivano da jacamo 
    private readonly object _queueLock = new object();

    // Aggiungi in cima alla classe:
    private ShopperAvatarScript _shopper;

  

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
        StartCoroutine(WaitUntilReadyThenTransmit(snapshot));//non trasmette subito, aspetta che ci sia qualcuno in ascolto(corutine)
    }

    // --------------------------------------------------------
    // WRAPPER DI COMPATIBILITÀ
    // --------------------------------------------------------
    public void TransmitGraph(//wrapper di compatibilità
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
    _shopper = GetComponent<ShopperAvatarScript>();
    
        // comportamento normale — apre il suo server (usato dall'explorer)
        initializeWebSocketConnection(OnMessageFromJacamo);
        StartCoroutine(StartServerCoroutine());
    
    // se useShopperChannel == true, non apre nessun server
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
        if (_shopper != null)
        {
            _shopper.ReachDestination(target);
            StartCoroutine(WaitAndNotify(target));
        }
    }
}

private IEnumerator WaitAndNotify(string target)
{
    // Aspetta che il NavMesh del GameObject principale finisca
    yield return null;
    yield return new WaitForSeconds(0.5f);
    
    var navAgent = _shopper.GetComponent<UnityEngine.AI.NavMeshAgent>();
    if (navAgent != null)
    {
        yield return new WaitUntil(() =>
            !navAgent.pathPending &&
            navAgent.remainingDistance <= navAgent.stoppingDistance &&
            (!navAgent.hasPath || navAgent.velocity.sqrMagnitude == 0f)
        );
    }
    
    Debug.Log($"[BeliefTransmitter] Arrivato a {target}, notifico JaCaMo");
    SendMovementCompleted(target);
}
// --------------------------------------------------------
// Notifica a JaCaMo che il movimento è completato.
// Manda un "signal" che VesnaAgent traduce in
// movement(completed, destination_reached).
// --------------------------------------------------------
private string _lastArrivedNode = null;
public string LastArrivedNode => _lastArrivedNode;
public void SendMovementCompleted(string targetNode)
{
    _lastArrivedNode = targetNode;
    
    wsChannel?.sendMessage(
        UnityJacamoIntegrationUtil.CreateAndConvertJacamoMessageIntoJsonString(
            "destinationReached", null, "reached_destination", "vesna_agent", targetNode
        )
    );
    SendCurrentRoom(targetNode);
}

private const float _readyPollInterval = 0.5f;
private const float _readyMaxWait      = 30f;

private IEnumerator WaitUntilReadyThenTransmit(BuildingSnapshot snapshot)
{
    float elapsed = 0f;

    while (!IsServerReady())
    {
        if (elapsed >= _readyMaxWait)
        {
            Debug.LogError("[BeliefTransmitter] Timeout: server non pronto. Trasmissione annullata.");
            yield break;
        }
        Debug.Log($"[BeliefTransmitter] Attesa server... ({elapsed:F1}s)");
        yield return new WaitForSeconds(_readyPollInterval);
        elapsed += _readyPollInterval;
    }

    while (!IsClientConnected())
    {
        if (elapsed >= _readyMaxWait)
        {
            Debug.LogError("[BeliefTransmitter] Timeout: nessun client connesso. Trasmissione annullata.");
            yield break;
        }
        Debug.Log($"[BeliefTransmitter] Attesa client... ({elapsed:F1}s)");
        yield return new WaitForSeconds(_readyPollInterval);
        elapsed += _readyPollInterval;
    }

    Debug.Log("[BeliefTransmitter] Server pronto e client connesso. Avvio trasmissione.");
    yield return StartCoroutine(TransmitSnapshotCoroutine(snapshot));
}

private bool IsServerReady()      => wsChannel != null && wsChannel.IsServerRunning;
private bool IsClientConnected()  => wsChannel != null && wsChannel.HasConnectedClients;

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

                            var poleB = floor.nodes.Find(n =>
                                n.corridorId == node.corridorId && n.nodeType == "CorridorPoleB");
                            if (poleB == null) continue;
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
                        //{ "explorationOrder",edge.explorationOrder },
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
    string fullMsg = UnityJacamoIntegrationUtil.CreateAndConvertJacamoMessageIntoJsonString(
        msg.beliefType, null, null, receiver, json
    );

    if (receiver == "vesna_agent" 
        && _shopper != null 
        && _shopper.WsChannel != null 
        && _shopper.WsChannel.IsServerRunning
        && _shopper.WsChannel.HasConnectedClients)
    {
        _shopper.SendMessageToJaCaMoBrain(fullMsg);
    }
    else
    {
        // fallback: usa il proprio canale (che è sempre attivo sulla 8081)
        wsChannel?.sendMessage(fullMsg);
    }
}

private void SendBelief(BeliefMessage msg) => SendBeliefTo(msg, "explorer");
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
    // usa sempre l'ultimo nodo arrivato se disponibile
    string toSend = _lastArrivedNode ?? roomId;
    SendBeliefTo(new BeliefMessage
    {
        beliefType = "current_room",
        payload = new Dictionary<string, object> { { "roomId", toSend } }
    }, "vesna_agent");
    Debug.Log($"[BeliefTransmitter] Stanza corrente inviata: {toSend}");
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