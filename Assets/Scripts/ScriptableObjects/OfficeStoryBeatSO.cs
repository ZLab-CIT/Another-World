using UnityEngine;

public enum OfficeStoryKind
{
    Gossip,
    SmallCrisis,
    Discovery,
    SharedPlan,
    Celebration
}

[CreateAssetMenu(menuName = "Another World/Office Story Beat", fileName = "NewOfficeStory")]
public class OfficeStoryBeatSO : ScriptableObject
{
    [Header("Identity")]
    public string storyId;
    public string title = "Office Story";
    public OfficeStoryKind kind;
    [Min(0.01f)] public float weight = 1f;

    [Header("Dialogue")]
    [Tooltip("Short subject used by local dialogue and optional LLM replies.")]
    [TextArea(1, 2)] public string topic;

    [Header("World Impact")]
    [Tooltip("Memory written when the story begins. Supports {speaker} and {target}.")]
    [TextArea(1, 3)] public string memory;
    [Tooltip("Public stories receive an announcement; private gossip remains visible only through character behavior.")]
    public bool publicAnnouncement;

    [Header("Optional Physical Consequence")]
    [Tooltip("Queues a real scene action when this story starts. Leave disabled for dialogue-only stories.")]
    public bool requiresAction;
    public OfficeActionType requiredAction = OfficeActionType.Custom;
    [Tooltip("Optional preferred agent id. When unavailable, the selected story speaker acts.")]
    public string preferredActorAgentId;
    [Min(2f)] public float actionDurationSeconds = 5f;

}
