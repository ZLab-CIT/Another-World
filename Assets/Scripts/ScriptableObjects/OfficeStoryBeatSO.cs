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

    [Header("Optional Follow-Through")]
    [Tooltip("When greater than zero, the same characters later gather at a social point.")]
    [Min(0f)] public float followUpDelaySeconds;
    [Tooltip("Memory recorded when the follow-up actually begins.")]
    [TextArea(1, 3)] public string followUpMemory;
}
