using System.Collections.Generic;
using System.Text;
using UnityEngine;

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
    [SerializeField] private string[] traits = new string[0];
    [SerializeField] private string[] interests = new string[0];
    [TextArea(2, 4)]
    [SerializeField] private string speechStyle = "";

    public string AgentId => agentId;
    public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? agentId : displayName;
    public string AgentType => agentType;
    public string Role => role;
    public string Biography => biography;
    public IReadOnlyList<string> Traits => traits;
    public IReadOnlyList<string> Interests => interests;
    public string SpeechStyle => speechStyle;

    public string BuildPromptDescription()
    {
        StringBuilder description = new();

        Append(description, "Role", role);
        Append(description, "Background", biography);
        Append(description, "Traits", Join(traits));
        Append(description, "Interests", Join(interests));
        Append(description, "Speech style", speechStyle);

        return description.ToString();
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

    private void OnValidate()
    {
        agentId = (agentId ?? "").Trim();
        displayName = (displayName ?? "").Trim();
        agentType = (agentType ?? "").Trim();
        role = (role ?? "").Trim();
    }
}
