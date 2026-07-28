using System.Collections.Generic;
using System.Text;
using UnityEngine;

[System.Serializable]
public struct AgentActionPreference
{
    public OfficeActionType actionType;
    [Range(-20f, 20f)] public float scoreModifier;
}

[CreateAssetMenu(menuName = "Another World/Agent Profile", fileName = "NewAgentProfile")]
public class AgentProfileSO : ScriptableObject
{
    [Header("Identity")]
    [SerializeField] private string agentId = "";
    [SerializeField] private string displayName = "";
    [SerializeField] private string agentType = "";
    [SerializeField] private string role = "";

    [Header("Character")]
    [TextArea(2, 5)]
    [SerializeField] private string biography = "";
    [Tooltip("Optional in-world birthday, for example 'October 12'. Leave empty until it is part of the character canon.")]
    [SerializeField] private string birthday = "";
    [SerializeField] private string[] traits = new string[0];
    [SerializeField] private string[] interests = new string[0];
    [TextArea(2, 4)]
    [SerializeField] private string speechStyle = "";

    [Header("Social Life")]
    [Tooltip("Established relationships, written from this character's point of view.")]
    [TextArea(1, 3)]
    [SerializeField] private string[] relationshipNotes = new string[0];
    [Tooltip("Specific experiences this character may naturally remember and refer to.")]
    [TextArea(1, 3)]
    [SerializeField] private string[] memorySeeds = new string[0];
    [Header("Autonomous Behavior")]
    [Tooltip("Token-free utility score adjustments that make this character prefer or avoid specific activities.")]
    [SerializeField] private AgentActionPreference[] actionPreferences =
        new AgentActionPreference[0];

    public string AgentId => agentId;
    public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? agentId : displayName;
    public string AgentType => agentType;
    public string Role => role;
    public string Biography => biography;
    public string Birthday => birthday;
    public IReadOnlyList<string> Traits => traits;
    public IReadOnlyList<string> Interests => interests;
    public IReadOnlyList<string> MemorySeeds => memorySeeds;
    public string SpeechStyle => speechStyle;

    public string BuildPromptDescription()
    {
        StringBuilder description = new();

        Append(description, "Role", role);
        Append(description, "Background", biography);
        Append(description, "Birthday", birthday);
        Append(description, "Traits", Join(traits));
        Append(description, "Interests", Join(interests));
        Append(description, "Speech style", speechStyle);

        return description.ToString();
    }

    public string BuildConversationSummary()
    {
        StringBuilder summary = new();
        Append(summary, "Role", role);
        Append(summary, "Traits", Join(traits));
        Append(summary, "Interests", Join(interests));
        Append(summary, "Speech style", speechStyle);
        return summary.ToString();
    }

    public string BuildRelationshipSummary()
    {
        return Join(relationshipNotes);
    }

    public int ScoreTopicRelevance(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return 0;

        int score = Contains(topic, role) ? 2 : 0;
        score += CountMatches(topic, interests) * 3;
        score += CountMatches(topic, traits);
        return score;
    }

    public float GetActionPreference(OfficeActionType actionType)
    {
        if (actionPreferences == null)
            return 0f;

        float result = 0f;
        foreach (AgentActionPreference preference in actionPreferences)
            if (preference.actionType == actionType)
                result += preference.scoreModifier;
        return result;
    }

    private static void Append(StringBuilder target, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (target.Length > 0)
            target.Append(' ');

        target.Append(label).Append(": ").Append(value.Trim()).Append('.');
    }

    private static string Join(string[] values)
    {
        return values == null ? "" : string.Join(", ", values);
    }

    private static int CountMatches(string topic, string[] values)
    {
        if (values == null)
            return 0;

        int count = 0;
        foreach (string value in values)
            if (Contains(topic, value))
                count++;
        return count;
    }

    private static bool Contains(string topic, string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && topic.IndexOf(value.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void OnValidate()
    {
        agentId = (agentId ?? "").Trim();
        displayName = (displayName ?? "").Trim();
        agentType = (agentType ?? "").Trim();
        role = (role ?? "").Trim();
        birthday = (birthday ?? "").Trim();
    }
}
