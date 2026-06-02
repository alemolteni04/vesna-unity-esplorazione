using UnityEngine;
using Newtonsoft.Json;

// ============================================================
// RoomObjectBridge.cs
// ============================================================
// Collega un oggetto fisico Unity al suo artefatto JaCaMo
// (RoomObjectArtifact) via WebSocket.
// Pattern identico a DoorVarcoArtifactBridge / CorridorArtifactBridge.
//
// FLUSSO:
//   • Prima volta che il cono visivo vede l'oggetto:
//     → BeliefTransmitter invia new_object all'agente Jason
//     → ExplorationManager registra l'oggetto nella lista _discoveredObjects
//     → l'agente crea dinamicamente RoomObjectArtifact e fa focus
//     → belief in_room(ArtifactId, RoomId) entra nel belief base
//
//   • Viste successive nella STESSA stanza:
//     → niente
//
//   • Vista in una STANZA DIVERSA:
//     → wsChannel invia update_room a RoomObjectArtifact.java
//     → ExplorationManager aggiorna la lista _discoveredObjects
//
// SETUP INSPECTOR:
//   • artifactName — ID univoco (es. "chair_lobby")
//   • port         — porta WebSocket (diversa per ogni oggetto)
//   • room         — trascina qui il GameObject della stanza
// ============================================================

public class RoomObjectBridge : AbstractMasElement
{
    [Header("Configurazione JaCaMo")]
    public string artifactName => gameObject.name;


[Header("Stanza di appartenenza")]
[Tooltip("Trascina qui il GameObject della stanza. L'ID è letto dal nome del GameObject.")]
public GameObject roomNode;

public string roomId => roomNode != null ? roomNode.name : "room_unknown";
    // ------------------------------------------------------------------
    // Stato interno
    // ------------------------------------------------------------------
    private bool   _discovered     = false;
    private string _lastSentRoomId = "";
    private ExplorationManager _explorationManager;
    private BeliefTransmitter  _beliefTransmitter;

    // ------------------------------------------------------------------
    // Unity lifecycle — pattern identico a DoorVarcoArtifactBridge
    // ------------------------------------------------------------------

    void Awake()
    {
        if (!Application.IsPlaying(gameObject)) return;
        objInUse = gameObject;

        initializeWebSocketConnection(OnMessageFromJacamo);
        _ = startServer();
    }

    void Start()
    {
        _explorationManager = FindObjectOfType<ExplorationManager>();
        _beliefTransmitter  = FindObjectOfType<BeliefTransmitter>();
    }

    // ------------------------------------------------------------------
    // API pubblica — chiamata da ExplorationManager.HandleRoomObjectVisible
    // ------------------------------------------------------------------

    public void NotifySeen()
    {
         Debug.Log($"[RoomObjectBridge] NotifySeen chiamato su '{artifactName}', discovered={_discovered}, roomId={roomId}");  // ← aggiunto

    if (!_discovered)
    {
        Debug.Log($"[RoomObjectBridge] Prima vista! Invio new_object per '{artifactName}' in '{roomId}'");  // ← aggiunto
        SendNewObject();
            _explorationManager?.RegisterDiscoveredObject(
                artifactName, roomId, int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);

            _discovered     = true;
            _lastSentRoomId = roomId;
        }
        else if (roomId != _lastSentRoomId)
        {
            // Stanza cambiata: aggiorna JaCaMo e snapshot
            SendUpdateRoom();
            _explorationManager?.RegisterDiscoveredObject(
                artifactName, roomId, int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);

            _lastSentRoomId = roomId;
        }
        // Stessa stanza: niente
    }

    // ------------------------------------------------------------------
    // Prima vista — via BeliefTransmitter (stesso canale degli altri belief)
    // ------------------------------------------------------------------

    private void SendNewObject()
    {
        if (_beliefTransmitter == null)
        {
            Debug.LogWarning($"[RoomObjectBridge] '{artifactName}': BeliefTransmitter non trovato.");
            return;
        }

        _beliefTransmitter.SendRoomObjectDiscovered(
            artifactName, roomId, int.Parse(port),
            transform.position.x, transform.position.y, transform.position.z);

        Debug.Log($"[RoomObjectBridge] '{artifactName}' → new_object in '{roomId}'");
    }

    // ------------------------------------------------------------------
    // Stanza cambiata — diretto su wsChannel, pattern DoorVarcoArtifactBridge
    // ------------------------------------------------------------------

    private void SendUpdateRoom()
    {
        var payload = new {
            operation    = "update_room",
            roomId       = this.roomId
        };

        wsChannel?.sendMessage(
            UnityJacamoIntegrationUtil.CreateAndConvertJacamoMessageIntoJsonString(
                "update_room", null, null,
                artifactName,
                payload
            )
        );

        Debug.Log($"[RoomObjectBridge] '{artifactName}' → update_room: '{roomId}'");
    }

    // ------------------------------------------------------------------
    // Ricezione da JaCaMo — pattern DoorVarcoArtifactBridge
    // ------------------------------------------------------------------

    private void OnMessageFromJacamo(object sender, WebSocketSharp.MessageEventArgs e)
    {
        // Nessun comando in entrata per ora.
        // Struttura pronta per future estensioni (es. "highlight", "move").
    }
}