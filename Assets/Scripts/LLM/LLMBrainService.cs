using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public struct AgentStateSnapshot
{
    public float energy;
    public float focus;
    public float social;
    public float productivity;
    public string mood;
    public string lastAction;
    public string relationships;
    public string coworkers;
}

public class ActionOption
{
    public string actionId;
    public string label;
}

public class AgentDecision
{
    public string actionId;
    public string targetAgent;
    public string reason;
}

public class AgentProfile
{
    public string agentId;
    public string displayName;
    public string personality;
    public readonly List<string> memory = new();
}

[Serializable]
public class AgentPersonalityEntry
{
    public string agentId;
    public string displayName;
    [TextArea] public string personality;
}

public class LLMBrainService : MonoBehaviour
{
    public static LLMBrainService Instance { get; private set; }

    [Header("Backend (OpenAI-compatible; Ollama by default)")]
    [SerializeField] private string baseUrl = "http://localhost:11434/v1";
    [SerializeField] private string apiKey = "";
    [Tooltip("Ollama model tag, e.g. qwen2.5:1.5b. For cloud, use the provider's model id.")]
    [SerializeField] private string model = "qwen2.5:1.5b";

    [Header("Decision")]
    [SerializeField] private float temperature = 0.8f;
    [Tooltip("Max tokens to generate. Keep small for speed: the decision JSON is ~30-40 tokens.")]
    [SerializeField] private int maxTokens = 64;
    [Tooltip("Seconds before an LLM request is abandoned (falls back to utility AI). Set high enough to survive the first cold model load (~15-30s) plus generation.")]
    [SerializeField] private int requestTimeoutSeconds = 60;
    [Tooltip("Number of recent memory lines included in each prompt.")]
    [SerializeField] private int memoryLines = 4;

    [Header("Social")]
    [Tooltip("If on, a targeted coworker generates a one-line reply (extra LLM call per social encounter). Turn off if your hardware lags.")]
    [SerializeField] private bool enableSocialReplies = true;

    [Header("Rate Limiting")]
    [Tooltip("Max concurrent in-flight LLM requests across all agents. Use 1 on CPU-only / weak hardware.")]
    [SerializeField] private int maxConcurrentRequests = 1;

    [Header("Agent Personalities")]
    [SerializeField] private AgentPersonalityEntry[] personalities;

    private ILLMBackend backend;
    private readonly Dictionary<string, AgentProfile> profiles = new();
    private int inFlight;

    public static LLMBrainService Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new(nameof(LLMBrainService));
        return go.AddComponent<LLMBrainService>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        backend = new OpenAICompatibleBackend(baseUrl, apiKey, model);

        if (personalities != null)
        {
            foreach (AgentPersonalityEntry entry in personalities)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.agentId))
                {
                    RegisterProfile(new AgentProfile
                    {
                        agentId = entry.agentId,
                        displayName = entry.displayName,
                        personality = entry.personality
                    });
                }
            }
        }
    }

    public void RegisterProfile(AgentProfile profile)
    {
        if (profile == null)
            return;

        if (!string.IsNullOrEmpty(profile.agentId))
            profiles[profile.agentId] = profile;

        if (!string.IsNullOrEmpty(profile.displayName))
            profiles[profile.displayName] = profile;
    }

    public AgentProfile GetProfile(string agentId)
    {
        profiles.TryGetValue(agentId, out AgentProfile profile);
        return profile;
    }

    public bool EnableSocialReplies => enableSocialReplies;

    public void Remember(string agentId, string line)
    {
        AgentProfile profile = GetProfile(agentId);
        if (profile != null && !string.IsNullOrEmpty(line))
            AppendMemory(profile, line);
    }

    public async Task<string> ReplyAsync(string agentId, string speakerName, string line)
    {
        if (backend == null || string.IsNullOrEmpty(line) || string.IsNullOrEmpty(speakerName))
            return null;

        AgentProfile profile = GetProfile(agentId);
        if (profile == null)
            return null;

        while (inFlight >= maxConcurrentRequests)
            await Task.Delay(100);

        inFlight++;

        try
        {
            string persona = string.IsNullOrEmpty(profile.personality)
                ? ""
                : profile.personality + " ";

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "You are " + (string.IsNullOrEmpty(profile.displayName) ? agentId : profile.displayName) +
                    " in a 2D office. " + persona +
                    "A coworker named " + speakerName + " just said something to you. " +
                    "Reply in character with ONE short sentence. No quotes, no JSON."),
                new ChatMessage("user", speakerName + " says: \"" + line + "\"")
            };

            LLMOptions opts = new()
            {
                temperature = temperature,
                maxTokens = 48,
                jsonMode = false,
                timeoutSeconds = requestTimeoutSeconds
            };

            string raw = await backend.CompleteAsync(messages, opts);
            return CleanReply(raw);
        }
        catch (Exception e)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " reply failed for " + agentId + ": " + e.Message);
            return null;
        }
        finally
        {
            inFlight--;
        }
    }

    public async Task<string> ConverseAsync(string agentId, string participants, string lastSpeaker, string lastLine)
    {
        if (backend == null || string.IsNullOrEmpty(lastLine))
            return null;

        AgentProfile profile = GetProfile(agentId);
        if (profile == null)
            return null;

        while (inFlight >= maxConcurrentRequests)
            await Task.Delay(100);

        inFlight++;

        try
        {
            string persona = string.IsNullOrEmpty(profile.personality)
                ? ""
                : profile.personality + " ";

            string who = string.IsNullOrEmpty(profile.displayName) ? agentId : profile.displayName;
            string present = string.IsNullOrEmpty(participants) ? "coworkers" : participants;

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "You are " + who + " in a 2D office. " + persona +
                    "You are in a group conversation with: " + present + ". " +
                    "Stay in character and continue with ONE short line. " +
                    "If the previous line asks you a question, answer it before changing topic. " +
                    "If you really need to get back to work and were not just asked a question, reply with exactly: LEAVE"),
                new ChatMessage("user", lastSpeaker + " said: \"" + lastLine + "\"")
            };

            LLMOptions opts = new()
            {
                temperature = temperature,
                maxTokens = 48,
                jsonMode = false,
                timeoutSeconds = requestTimeoutSeconds
            };

            string raw = await backend.CompleteAsync(messages, opts);
            string cleaned = CleanReply(raw);
            if (cleaned != null && cleaned.Equals("LEAVE", System.StringComparison.OrdinalIgnoreCase))
                return null;

            return cleaned;
        }
        catch (Exception e)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " converse failed for " + agentId + ": " + e.Message);
            return null;
        }
        finally
        {
            inFlight--;
        }
    }

    public async Task<string> OpenConversationAsync(string agentId, string present)
    {
        if (backend == null)
            return null;

        AgentProfile profile = GetProfile(agentId);
        if (profile == null)
            return null;

        while (inFlight >= maxConcurrentRequests)
            await Task.Delay(100);

        inFlight++;

        try
        {
            string persona = string.IsNullOrEmpty(profile.personality)
                ? ""
                : profile.personality + " ";

            string who = string.IsNullOrEmpty(profile.displayName) ? agentId : profile.displayName;
            string with = string.IsNullOrEmpty(present) ? "some coworkers" : present;

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "You are " + who + " in a 2D office. " + persona +
                    "You're taking a break with " + with + ". " +
                    "Say ONE short line to start a casual conversation in character. No quotes, no JSON."),
                new ChatMessage("user", "Start the conversation.")
            };

            LLMOptions opts = new()
            {
                temperature = temperature,
                maxTokens = 48,
                jsonMode = false,
                timeoutSeconds = requestTimeoutSeconds
            };

            string raw = await backend.CompleteAsync(messages, opts);
            return CleanReply(raw);
        }
        catch (Exception e)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " opener failed for " + agentId + ": " + e.Message);
            return null;
        }
        finally
        {
            inFlight--;
        }
    }

    private static string CleanReply(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            s = s.Substring(1, s.Length - 2).Trim();

        if (string.IsNullOrWhiteSpace(s))
            return null;

        return s;
    }

    public async Task<AgentDecision> ChooseActionAsync(
        string agentId,
        AgentStateSnapshot state,
        List<ActionOption> options)
    {
        if (backend == null || options == null || options.Count == 0)
            return null;

        AgentProfile profile = GetProfile(agentId);
        if (profile == null)
            return null;

        while (inFlight >= maxConcurrentRequests)
            await Task.Delay(100);

        inFlight++;

        try
        {
            List<ChatMessage> messages = BuildDecisionMessages(profile, state, options);
            LLMOptions opts = new()
            {
                temperature = temperature,
                maxTokens = maxTokens,
                jsonMode = true,
                timeoutSeconds = requestTimeoutSeconds
            };

            string raw = await backend.CompleteAsync(messages, opts);
            AgentDecision decision = ParseDecision(raw);
            if (decision != null && !IsValidActionId(decision.actionId, options))
                decision = null;

            if (decision != null)
            {
                string memLine = "Chose " + decision.actionId;
                if (!string.IsNullOrEmpty(decision.targetAgent))
                    memLine += " with " + decision.targetAgent;
                if (!string.IsNullOrEmpty(decision.reason))
                    memLine += " (" + decision.reason + ")";
                AppendMemory(profile, memLine);
            }

            return decision;
        }
        catch (Exception e)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " decision failed for " + agentId + ": " + e.Message);
            return null;
        }
        finally
        {
            inFlight--;
        }
    }

    private List<ChatMessage> BuildDecisionMessages(
        AgentProfile profile, AgentStateSnapshot state, List<ActionOption> options)
    {
        List<ChatMessage> messages = new();

        messages.Add(new ChatMessage("system",
            "You roleplay a worker in a 2D office simulation. Stay in character and pick ONE action to do next. " +
            "Respond ONLY with JSON: {\"actionId\": string, \"targetAgent\": string, \"reason\": string}. " +
            "actionId MUST be exactly one of the listed actions (verbatim, no other value). If actionId is ChatSpot, targetAgent MUST be the name of one of the coworkers present (pick someone specific); otherwise targetAgent is empty. " +
            "reason is a SHORT (one sentence) in-character line. If actionId is ChatSpot or BreakSpot, make it something you'd SAY out loud to start a conversation with anyone there; otherwise it's an inner thought or mutter. " +
            "Vary your tone: gripe, joke, observe, daydream, or react. Do NOT just justify the action (e.g. \"need coffee\"). Avoid repeating the same opening.\n\n" +
            "Your profile: " + profile.personality));

        StringBuilder sb = new();
        sb.Append("Status - energy: ").Append(Mathf.RoundToInt(state.energy));
        sb.Append(", focus: ").Append(Mathf.RoundToInt(state.focus));
        sb.Append(", social: ").Append(Mathf.RoundToInt(state.social));
        sb.Append(", productivity: ").Append(Mathf.RoundToInt(state.productivity));
        sb.Append(", mood: ").Append(string.IsNullOrEmpty(state.mood) ? "neutral" : state.mood);
        sb.Append(", just did: ").Append(string.IsNullOrEmpty(state.lastAction) ? "nothing yet" : state.lastAction);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(state.relationships))
        {
            sb.Append("Relationships (name and closeness): ");
            sb.Append(state.relationships);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(state.coworkers))
        {
            sb.Append("Coworkers present: ");
            sb.Append(state.coworkers);
            sb.AppendLine();
        }

        if (profile.memory.Count > 0)
        {
            sb.Append("Recent memory:");
            int start = Mathf.Max(0, profile.memory.Count - memoryLines);
            for (int i = start; i < profile.memory.Count; i++)
                sb.Append("\n- ").Append(profile.memory[i]);
            sb.AppendLine();
        }

        sb.Append("Available actions:");
        foreach (ActionOption opt in options)
        {
            sb.Append("\n- ").Append(opt.actionId);
            if (!string.IsNullOrEmpty(opt.label))
                sb.Append(" (").Append(opt.label).Append(")");
        }

        messages.Add(new ChatMessage("user", sb.ToString()));
        return messages;
    }

    private static bool IsValidActionId(string actionId, List<ActionOption> options)
    {
        if (string.IsNullOrEmpty(actionId))
            return false;

        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].actionId, actionId, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private AgentDecision ParseDecision(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string json = raw.Trim();
        if (json.StartsWith("```"))
        {
            int firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0)
                json = json.Substring(firstNewline + 1);

            int lastFence = json.LastIndexOf("```");
            if (lastFence >= 0)
                json = json.Substring(0, lastFence);

            json = json.Trim();
        }

        try
        {
            DecisionDTO dto = JsonUtility.FromJson<DecisionDTO>(json);
            if (dto == null || string.IsNullOrEmpty(dto.actionId))
                return null;

            return new AgentDecision
            {
                actionId = dto.actionId,
                targetAgent = dto.targetAgent ?? "",
                reason = dto.reason ?? ""
            };
        }
        catch
        {
            return null;
        }
    }

    private void AppendMemory(AgentProfile profile, string line)
    {
        profile.memory.Add(line);

        const int cap = 30;
        while (profile.memory.Count > cap)
            profile.memory.RemoveAt(0);
    }

    [ContextMenu("Test Connection")]
    private async void TestConnection()
    {
        backend ??= new OpenAICompatibleBackend(baseUrl, apiKey, model);

        List<ChatMessage> messages = new()
        {
            new ChatMessage("system", "Respond ONLY with JSON: {\"ok\": true, \"msg\": string}."),
            new ChatMessage("user", "Say hello in one short sentence.")
        };

        LLMOptions opts = new() { temperature = 0.5f, maxTokens = 64, jsonMode = true };
        string result = await backend.CompleteAsync(messages, opts);

        Debug.Log(nameof(LLMBrainService) + " test response: " + (result ?? "(null/failed - is Ollama running?)"));
    }

    [Serializable]
    private class DecisionDTO
    {
        public string actionId;
        public string targetAgent;
        public string reason;
    }
}
