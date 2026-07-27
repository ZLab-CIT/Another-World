using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

public sealed class ConversationPlan
{
    public string targetAgent;
    public string topic;
    public string openingLine;
}
public sealed class ConversationParticipantContext
{
    public string agentId;
    public string displayName;
    public string relationships;
}

public sealed class ConversationTurn
{
    public string speaker;
    public string line;
}

public sealed class SocialMemoryEntry
{
    public string type;
    public string sourceAgent;
    public string targetAgent;
    public string subject;
    public bool isPrivate;
    public string status;
}

public sealed class ConversationScript
{
    public readonly List<ConversationTurn> turns = new();
    public readonly List<SocialMemoryEntry> socialEvents = new();
}

public sealed class OfficeActivityPlan
{
    public OfficeActionType actionType;
    public OfficeDestinationMode destinationMode;
    public string destinationHint;
    public string sequenceId;
    public int sequenceStep;
    public string objective;
    public string targetAgent;
    public float durationSeconds;
    public string reason;
    public string thought;
    public string customActionLabel;
    public float energyChange;
    public float focusChange;
    public float socialChange;
    public float productivityChange;
    public string socialMemorySubject;
    public bool completesSocialCommitment;
    public string socialOpeningLine;
}

public enum OfficeDestinationMode
{
    ActionPoint,
    FreePosition,
    CurrentPosition,
    FollowAgent
}

public class AgentProfile
{
    public string agentId;
    public string displayName;
    public string personality;
    public string birthday;
    public readonly List<string> memory = new();
    public readonly List<SocialMemoryEntry> socialMemory = new();
    public readonly List<string> recentTopics = new();
    public readonly List<string> recentOpenings = new();
    public readonly List<string> recentUtterances = new();
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

    [Header("Backend (OpenAI-compatible; GLM by default)")]
    [SerializeField] private string baseUrl = "https://api.z.ai/api/paas/v4";
    [SerializeField] private string apiKey = "";
    [Tooltip("Environment variable used when Api Key is empty. Checked before GLM_API_KEY, ZHIPUAI_API_KEY, and ZAI_API_KEY fallbacks.")]
    [SerializeField] private string apiKeyEnvironmentVariable = "GLM_API_KEY";
    [Tooltip("GLM model id. glm-4.7-flash is the free model; glm-4.7 requires account balance or a resource package.")]
    [SerializeField] private string model = "glm-4.7-flash";

    [Header("Generation")]
    [SerializeField] private float temperature = 0.8f;
    [Tooltip("Seconds before an LLM request is abandoned (falls back to utility AI). Set high enough to survive the first cold model load (~15-30s) plus generation.")]
    [SerializeField] private int requestTimeoutSeconds = 60;
    [Tooltip("Number of recent memory lines included in each prompt.")]
    [SerializeField] private int memoryLines = 4;

    [Header("Social")]
    [Tooltip("If on, the model writes the continuation of each conversation. Turn off for fully local dialogue.")]
    [SerializeField] private bool enableSocialReplies = true;
    [Tooltip("If on, the model also chooses the partner, topic, and opening before travel. This costs a second request per conversation and is not recommended for small CPU-hosted models.")]
    [SerializeField] private bool enableGeneratedConversationPlans = false;
    [Tooltip("A generated social plan gives up quickly and uses a local character-specific plan instead.")]
    [SerializeField, Min(2)] private int conversationPlanTimeoutSeconds = 8;
    [Tooltip("Maximum generation time for the complete three-reply conversation script.")]
    [SerializeField, Min(8)] private int conversationScriptTimeoutSeconds = 30;

    [Header("Activity Planning")]
    [Tooltip("If on, the model generates short queues of office activities. Unity still validates every target and path when each activity starts.")]
    [SerializeField] private bool enableActivityPlans = true;
    [SerializeField, Range(2, 6)] private int activityBatchSize = 3;
    [Tooltip("Minimum time between activity-batch requests across every worker in the office. A longer pause reduces token bursts on CPU-hosted models.")]
    [SerializeField, Min(2f)] private float globalActivityPlanIntervalSeconds = 12f;
    [SerializeField, Min(10)] private int activityPlanTimeoutSeconds = 30;

    [Header("Agent Personalities")]
    [SerializeField] private AgentPersonalityEntry[] personalities;

    private ILLMBackend backend;
    private readonly Dictionary<string, AgentProfile> profiles = new();
    private readonly List<string> worldEvents = new();
    private readonly List<string> recentGlobalTopics = new();
    private readonly List<string> recentGlobalUtterances = new();
    private readonly HashSet<string> announcedBirthdays = new();
    private readonly List<string> pendingActivityRequesters = new();
    private bool loggedMissingApiKey;
    private float nextGlobalActivityPlanTime;

    private LLMConversationPlanner conversationPlanner;
    private LLMActivityPlanner activityPlanner;

    internal Dictionary<string, AgentProfile> Profiles => profiles;
    internal List<string> WorldEvents => worldEvents;
    internal List<string> RecentGlobalTopics => recentGlobalTopics;
    internal List<string> RecentGlobalUtterances => recentGlobalUtterances;

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
        string resolvedApiKey = ResolveApiKey();
        bool backendReady = !RequiresApiKey() || !string.IsNullOrWhiteSpace(resolvedApiKey);
        if (backendReady)
        {
            backend = new OpenAICompatibleBackend(baseUrl, resolvedApiKey, model);
        }
        else
        {
            LogMissingApiKey();
            backend = null;
        }

        conversationPlanner = new LLMConversationPlanner(this, backend);
        activityPlanner = new LLMActivityPlanner(this, backend);

        Debug.Log("[LLMBrainService] backend=" + (backendReady ? model : "NONE (missing API key)")
            + " | enableSocialReplies=" + enableSocialReplies
            + " | enableGeneratedConversationPlans=" + enableGeneratedConversationPlans
            + " | enableActivityPlans=" + enableActivityPlans, this);

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

        RememberBirthdayIfToday(profile);
    }

    public AgentProfile GetProfile(string agentId)
    {
        profiles.TryGetValue(agentId, out AgentProfile profile);
        return profile;
    }

    public bool EnableSocialReplies => enableSocialReplies;
    public bool EnableGeneratedConversationPlans => enableGeneratedConversationPlans;
    public bool EnableActivityPlans => enableActivityPlans;
    public int ActivityBatchSize => Mathf.Clamp(activityBatchSize, 2, 6);

    public bool TryReserveActivityPlanRequest(string agentId)
    {
        if (!enableActivityPlans || backend == null || string.IsNullOrWhiteSpace(agentId))
            return false;

        string requester = agentId.Trim();
        if (!pendingActivityRequesters.Contains(requester))
            pendingActivityRequesters.Add(requester);

        if (pendingActivityRequesters.Count == 0
            || !string.Equals(pendingActivityRequesters[0], requester,
                StringComparison.OrdinalIgnoreCase)
            || Time.unscaledTime < nextGlobalActivityPlanTime)
            return false;

        pendingActivityRequesters.RemoveAt(0);
        nextGlobalActivityPlanTime = Time.unscaledTime
            + Mathf.Max(2f, globalActivityPlanIntervalSeconds);
        return true;
    }

    public void CancelActivityPlanRequest(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return;

        pendingActivityRequesters.RemoveAll(value => string.Equals(
            value, agentId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void Remember(string agentId, string line)
    {
        AgentProfile profile = GetProfile(agentId);
        if (profile != null && !string.IsNullOrEmpty(line))
            AppendMemory(profile, line);
    }

    public void RememberWorldEvent(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return;
        worldEvents.Add(description.Trim());
        while (worldEvents.Count > 12)
            worldEvents.RemoveAt(0);
    }

    public async Task<ConversationPlan> PlanConversationAsync(
        string agentId,
        OfficeActionType location,
        List<ConversationParticipantContext> coworkers,
        string relationships,
        string currentState)
    {
        return await conversationPlanner.PlanConversationAsync(
            agentId, location, coworkers, relationships, currentState,
            Mathf.Max(2, memoryLines), temperature, requestTimeoutSeconds,
            conversationPlanTimeoutSeconds);
    }

    public async Task<List<OfficeActivityPlan>> PlanActivityBatchAsync(
        string agentId,
        List<OfficeActionType> availableActions,
        List<ConversationParticipantContext> coworkers,
        string currentState,
        int requestedCount)
    {
        return await activityPlanner.PlanActivityBatchAsync(
            agentId, availableActions, coworkers, currentState, requestedCount,
            Mathf.Max(2, memoryLines), temperature, requestTimeoutSeconds,
            activityPlanTimeoutSeconds);
    }

    public async Task<ConversationScript> GenerateConversationAsync(
        List<ConversationParticipantContext> participants,
        string openingSpeaker,
        string openingLine,
        string topic,
        List<string> speakerOrder)
    {
        return await conversationPlanner.GenerateConversationAsync(
            participants, openingSpeaker, openingLine, topic, speakerOrder,
            Mathf.Max(2, memoryLines), temperature, requestTimeoutSeconds,
            conversationScriptTimeoutSeconds);
    }

    public async Task<List<string>> GenerateEventMonologueAsync(
        string agentId, string purpose, string context, int lineCount)
    {
        return await conversationPlanner.GenerateEventMonologueAsync(
            agentId, purpose, context, lineCount, Mathf.Max(2, memoryLines),
            temperature, requestTimeoutSeconds, conversationScriptTimeoutSeconds);
    }

    public bool IsFreshOpening(string agentId, string line)
    {
        return conversationPlanner.IsFreshOpening(agentId, line);
    }

    public void RecordConversation(List<ConversationParticipantContext> participants,
        string topic, string openingSpeaker, string openingLine, List<ConversationTurn> turns,
        List<SocialMemoryEntry> socialEvents = null)
    {
        conversationPlanner.RecordConversation(participants, topic, openingSpeaker,
            openingLine, turns, socialEvents);
    }

    public void CompleteSocialCommitment(string agentId, string subject)
    {
        AgentProfile profile = GetProfile(agentId);
        if (profile == null || string.IsNullOrWhiteSpace(subject))
            return;

        SocialMemoryEntry completed = null;
        foreach (SocialMemoryEntry entry in profile.socialMemory)
        {
            if (entry == null || entry.status != "open"
                || TextUtils.TextSimilarity(entry.subject, subject) < 0.7f)
                continue;
            completed = entry;
            break;
        }
        if (completed == null)
            return;

        HashSet<AgentProfile> uniqueProfiles = new();
        foreach (AgentProfile candidateProfile in profiles.Values)
            if (candidateProfile != null)
                uniqueProfiles.Add(candidateProfile);
        foreach (AgentProfile candidateProfile in uniqueProfiles)
            foreach (SocialMemoryEntry entry in candidateProfile.socialMemory)
                if (entry != null && entry.status == "open"
                    && string.Equals(entry.sourceAgent, completed.sourceAgent,
                        StringComparison.OrdinalIgnoreCase)
                    && TextUtils.TextSimilarity(entry.subject, completed.subject) >= 0.7f)
                    entry.status = "completed";

        Debug.Log("[Social memory completed] " + TextUtils.DisplayName(profile, agentId) + ": " +
            TextUtils.FormatSocialMemory(completed), this);
    }

    private void RememberBirthdayIfToday(AgentProfile profile)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.birthday)
            || !announcedBirthdays.Add(profile.agentId ?? profile.displayName ?? profile.birthday))
            return;

        bool parsed = DateTime.TryParse(profile.birthday, CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces, out DateTime birthday);
        if (!parsed)
            parsed = DateTime.TryParse(profile.birthday, CultureInfo.GetCultureInfo("en-US"),
                DateTimeStyles.AllowWhiteSpaces, out birthday);

        DateTime today = DateTime.Today;
        if (parsed && birthday.Month == today.Month && birthday.Day == today.Day)
        {
            string name = string.IsNullOrWhiteSpace(profile.displayName) ? profile.agentId : profile.displayName;
            RememberWorldEvent("Today is " + name + "'s birthday.");
        }
    }

    private void AppendMemory(AgentProfile profile, string line)
    {
        int recentStart = Mathf.Max(0, profile.memory.Count - 8);
        for (int i = recentStart; i < profile.memory.Count; i++)
        {
            if (string.Equals(profile.memory[i], line, StringComparison.OrdinalIgnoreCase))
                return;
        }

        profile.memory.Add(line);

        const int cap = 30;
        while (profile.memory.Count > cap)
            profile.memory.RemoveAt(0);
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            return apiKey.Trim();

        string[] candidates =
        {
            apiKeyEnvironmentVariable,
            "GLM_API_KEY",
            "ZHIPUAI_API_KEY",
            "ZAI_API_KEY"
        };

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string variableName = candidate.Trim();
            string value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(
                    variableName, EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(
                    variableName, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private bool RequiresApiKey()
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        Uri uri;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri))
            return false;

        return !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private void LogMissingApiKey()
    {
        if (loggedMissingApiKey)
            return;

        loggedMissingApiKey = true;
        Debug.LogWarning(nameof(LLMBrainService) +
            " GLM API key is empty. Set Api Key in the Inspector or launch Unity with GLM_API_KEY, ZHIPUAI_API_KEY, or ZAI_API_KEY set.",
            this);
    }

    [ContextMenu("Test Connection")]
    private async void TestConnection()
    {
        string resolvedApiKey = ResolveApiKey();
        if (RequiresApiKey() && string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            LogMissingApiKey();
            Debug.Log(nameof(LLMBrainService) + " test response: (not sent - missing GLM API key)", this);
            return;
        }

        backend = new OpenAICompatibleBackend(baseUrl, resolvedApiKey, model);
        conversationPlanner = new LLMConversationPlanner(this, backend);
        activityPlanner = new LLMActivityPlanner(this, backend);

        List<ChatMessage> messages = new()
        {
            new ChatMessage("system", "Respond ONLY with JSON: {\"ok\": true, \"msg\": string}."),
            new ChatMessage("user", "Say hello in one short sentence.")
        };

        LLMOptions opts = new()
        {
            requestLabel = "BackendTest",
            temperature = 0.5f,
            maxTokens = 64,
            jsonMode = true
        };
        string result = await backend.CompleteAsync(messages, opts);

        Debug.Log(nameof(LLMBrainService) + " test response: " +
            (result ?? "(null/failed - check GLM API key, quota, base URL, and model access)"));
    }
}
