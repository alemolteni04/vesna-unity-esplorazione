using UnityEngine;

public class RoomObjectArtifact : Artifact
{
    private bool   _discovered     = false;
    private string _lastSentRoomId = "";
    private ExplorationManager _explorationManager;
    private BeliefTransmitter  _beliefTransmitter;

    protected override void Awake()
    {
        artifactType = ArtifactTypeEnum.RoomObject;
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
                gameObject.name, roomId, artifactType.ToString(), int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);
            _explorationManager?.RegisterDiscoveredObject(
                gameObject.name, roomId, artifactType.ToString(), int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);
            _discovered     = true;
            _lastSentRoomId = roomId;
        }
        else if (roomId != _lastSentRoomId)
        {
            _explorationManager?.RegisterDiscoveredObject(
                gameObject.name, roomId, artifactType.ToString(), int.Parse(port),
                transform.position.x, transform.position.y, transform.position.z);
            _lastSentRoomId = roomId;
        }
    }
}