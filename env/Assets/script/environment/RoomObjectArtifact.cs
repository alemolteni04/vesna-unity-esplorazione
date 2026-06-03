using UnityEngine;

public class RoomObjectArtifact : Artifact
{
    [Header("Stanza di appartenenza")]
    [Tooltip("Trascina qui il GameObject della stanza.")]
    public GameObject roomNode;

    public string roomId => roomNode != null ? roomNode.name : "room_unknown";

    private bool   _discovered     = false;
    private string _lastSentRoomId = "";
    private ExplorationManager _explorationManager;
    private BeliefTransmitter  _beliefTransmitter;

    protected override void Awake()
    {
        artifactType = ArtifactTypeEnum.RoomObject; // <-- aggiungi questa riga
        base.Awake();
    }

    void Start()
    {
        _explorationManager = FindObjectOfType<ExplorationManager>();
        _beliefTransmitter  = FindObjectOfType<BeliefTransmitter>();
    }

    public void NotifySeen()
    {
        if (!_discovered)
        {
            Debug.Log($"[RoomObjectArtifact] '{gameObject.name}' VISTO in '{roomId}'");
            _beliefTransmitter?.SendRoomObjectDiscovered(
                gameObject.name, roomId, int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);
            _explorationManager?.RegisterDiscoveredObject(
                gameObject.name, roomId, int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);
            _discovered     = true;
            _lastSentRoomId = roomId;
        }
        else if (roomId != _lastSentRoomId)
        {
            _explorationManager?.RegisterDiscoveredObject(
                gameObject.name, roomId, int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);
            _lastSentRoomId = roomId;
        }
    }
}