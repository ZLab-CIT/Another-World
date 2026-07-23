using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
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
    [SerializeField, Range(2, 6)] private int activityBatchSize = 4;
    [Tooltip("Minimum time between activity-batch requests across every worker in the office.")]
    [SerializeField, Min(5f)] private float globalActivityPlanIntervalSeconds = 30f;
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
    private readonly SemaphoreSlim socialRequestGate = new(1, 1);
    private int pendingConversationScripts;
    private bool loggedMissingApiKey;
    private float nextGlobalActivityPlanTime;

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
        if (RequiresApiKey() && string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            LogMissingApiKey();
            backend = null;
        }
        else
        {
            backend = new OpenAICompatibleBackend(baseUrl, resolvedApiKey, model);
        }

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
            || pendingConversationScripts > 0
            || socialRequestGate.CurrentCount == 0
            || Time.unscaledTime < nextGlobalActivityPlanTime)
            return false;

        pendingActivityRequesters.RemoveAt(0);
        nextGlobalActivityPlanTime = Time.unscaledTime
            + Mathf.Max(5f, globalActivityPlanIntervalSeconds);
        return true;
    }

    public void CancelActivityPlanRequest(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return;

        pendingActivityRequesters.RemoveAll(value => string.Equals(
            value, agentId.Trim(), StringComparison.OrdinalIgnoreCase));
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

    [ContextMenu("Debug/Log Memory Snapshot")]
    private void LogMemorySnapshot()
    {
        StringBuilder snapshot = new();
        snapshot.AppendLine("LLM memory snapshot");
        snapshot.AppendLine("World events:");
        if (worldEvents.Count == 0)
            snapshot.AppendLine("- <none>");
        else
            foreach (string worldEvent in worldEvents)
                snapshot.Append("- ").Append(worldEvent).AppendLine();

        HashSet<AgentProfile> uniqueProfiles = new();
        foreach (AgentProfile profile in profiles.Values)
            if (profile != null)
                uniqueProfiles.Add(profile);

        snapshot.AppendLine("Profiles:");
        foreach (AgentProfile profile in uniqueProfiles)
        {
            snapshot.Append("- ").Append(DisplayName(profile, profile.agentId)).Append(": ");
            snapshot.Append("birthday=").Append(string.IsNullOrWhiteSpace(profile.birthday)
                ? "<unset>" : profile.birthday.Trim());
            snapshot.Append(", memories=").Append(profile.memory.Count)
                .Append(", social memories=").Append(profile.socialMemory.Count).AppendLine();
            int start = Mathf.Max(0, profile.memory.Count - Mathf.Max(1, memoryLines));
            for (int i = start; i < profile.memory.Count; i++)
                snapshot.Append("  - ").Append(profile.memory[i]).AppendLine();
            int socialStart = Mathf.Max(0, profile.socialMemory.Count - Mathf.Max(1, memoryLines));
            for (int i = socialStart; i < profile.socialMemory.Count; i++)
                snapshot.Append("  - [social] ").Append(FormatSocialMemory(profile.socialMemory[i]))
                    .AppendLine();
        }

        Debug.Log(snapshot.ToString(), this);
    }

    [ContextMenu("Debug/Add Test Serious Event")]
    private void AddTestSeriousEvent()
    {
        RememberWorldEvent("A serious production incident happened today, and the office is treating it carefully.");
        LogMemorySnapshot();
    }

    [ContextMenu("Debug/Add Test Social Memory")]
    private void AddTestSocialMemory()
    {
        List<AgentProfile> available = new();
        foreach (AgentProfile profile in profiles.Values)
            if (profile != null && !available.Contains(profile))
                available.Add(profile);
        if (available.Count < 2)
        {
            Debug.LogWarning("Social memory test needs at least two registered agents in Play Mode.", this);
            return;
        }

        AgentProfile first = available[0];
        AgentProfile second = available[1];
        List<ConversationParticipantContext> participants = new()
        {
            new ConversationParticipantContext
            {
                agentId = first.agentId,
                displayName = DisplayName(first, first.agentId)
            },
            new ConversationParticipantContext
            {
                agentId = second.agentId,
                displayName = DisplayName(second, second.agentId)
            }
        };
        RecordSocialEvents(participants, new List<SocialMemoryEntry>
        {
            new SocialMemoryEntry
            {
                type = "invitation",
                sourceAgent = participants[0].displayName,
                targetAgent = participants[1].displayName,
                subject = "Meet for coffee near the lounge after this task.",
                status = "open"
            }
        });
        LogMemorySnapshot();
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

    public async Task<ConversationPlan> PlanConversationAsync(
        string agentId,
        OfficeActionType location,
        List<ConversationParticipantContext> coworkers,
        string relationships,
        string currentState)
    {
        AgentProfile profile = GetProfile(agentId);
        if (backend == null || profile == null || coworkers == null || coworkers.Count == 0)
            return null;

        // Planning is optional. Do not make an idle worker stand still behind a long
        // dialogue request; a fresh character-specific local plan is better here.
        if (socialRequestGate.CurrentCount == 0 || pendingConversationScripts > 0)
            return null;

        await socialRequestGate.WaitAsync();
        try
        {
            string who = DisplayName(profile, agentId);
            string candidateNames = JoinParticipantNames(coworkers);
            StringBuilder context = new();
            context.Append("Location: ").Append(location == OfficeActionType.BreakSpot
                ? "a shared break area" : "an office chat area").AppendLine();
            if (!string.IsNullOrWhiteSpace(currentState))
                context.Append("How you feel right now: ").Append(currentState.Trim()).AppendLine();
            if (!string.IsNullOrWhiteSpace(relationships))
                context.Append("Your relationships: ").Append(relationships.Trim()).AppendLine();
            context.AppendLine("Available coworkers:");
            foreach (ConversationParticipantContext coworker in coworkers)
            {
                if (coworker == null || string.IsNullOrWhiteSpace(coworker.displayName))
                    continue;
                AgentProfile coworkerProfile = GetProfile(coworker.agentId);
                context.Append("- ").Append(coworker.displayName).Append(": ")
                    .Append(coworkerProfile != null ? coworkerProfile.personality : "coworker");
                if (!string.IsNullOrWhiteSpace(coworker.relationships))
                    context.Append(" Relationship: ").Append(coworker.relationships.Trim());
                context.AppendLine();
            }
            AppendRecentMemory(context, profile, Mathf.Max(2, memoryLines));
            AppendSocialMemory(context, profile, Mathf.Max(2, memoryLines));
            AppendWorldEvents(context, 3);
            AppendRecentList(context, "Subjects you recently discussed; choose something different:",
                profile.recentTopics, 6);
            AppendRecentList(context, "Openings you recently used; do not paraphrase them:",
                profile.recentOpenings, 6);

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Plan one believable conversation for an office-life simulation. You are " + who + ". " +
                    profile.personality + " Choose a coworker and a specific subject this person would genuinely bring up now. " +
                    "Ground it in a personal interest, an established memory, that relationship, or a recent shared event. " +
                    "Do not use vague invitations, generic check-ins, motivational language, or a recently used subject. " +
                    "Use simple everyday English. The opening must be one sentence of 4 to 12 words and give the other person something concrete to answer. " +
                    "Express only one idea. Avoid metaphors, abstract advice, corporate language, semicolons, and long explanations. " +
                    "Do not invent a past event as fact. Respond only as JSON: " +
                    "{\"targetAgent\":string,\"topic\":string,\"openingLine\":string}. " +
                    "Use each key exactly once. If the opening uses a name, it must be the target's name, never your own. " +
                    "targetAgent must be exactly one of: " + candidateNames + "."),
                new ChatMessage("user", context.ToString())
            };

            LLMOptions options = new()
            {
                requestLabel = "ConversationPlan",
                temperature = Mathf.Clamp(temperature, 0.65f, 0.85f),
                maxTokens = 120,
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ConversationPlan,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds, conversationPlanTimeoutSeconds),
                maxRetries = 1,
                retryBaseDelaySeconds = 1.5f
            };
            string raw = await backend.CompleteAsync(messages, options);
            ConversationPlan plan = ParseConversationPlan(raw);
            string rejection = ValidateConversationPlan(plan, profile, coworkers, who,
                candidateNames, out ConversationPlan validated);
            if (rejection != null)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    Debug.LogWarning("[Conversation plan rejected] " + who + ": " + rejection, this);
                return null;
            }

            return validated;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " conversation planning failed for " +
                agentId + ": " + exception.Message, this);
            return null;
        }
        finally
        {
            socialRequestGate.Release();
        }
    }

    public async Task<List<OfficeActivityPlan>> PlanActivityBatchAsync(
        string agentId,
        List<OfficeActionType> availableActions,
        List<ConversationParticipantContext> coworkers,
        string currentState,
        int requestedCount)
    {
        AgentProfile profile = GetProfile(agentId);
        if (profile == null || availableActions == null || availableActions.Count == 0)
            return null;

        requestedCount = Mathf.Clamp(requestedCount, 2, 6);
        OfficeActivityPlan commitmentPlan = BuildOpenCommitmentPlan(
            profile, availableActions, coworkers);
        if (commitmentPlan != null
            && (ContainsIgnoreCase(commitmentPlan.socialMemorySubject, "coffee")
                || ContainsIgnoreCase(commitmentPlan.socialMemorySubject, "drink")))
            return SinglePlan(commitmentPlan);

        if (backend == null)
            return SinglePlan(commitmentPlan);

        if (socialRequestGate.CurrentCount == 0 || pendingConversationScripts > 0)
            return SinglePlan(commitmentPlan);

        await socialRequestGate.WaitAsync();
        try
        {
            string who = DisplayName(profile, agentId);
            string allowed = JoinActionTypes(availableActions);
            string coworkerNames = JoinParticipantNames(coworkers);
            StringBuilder context = new();
            if (!string.IsNullOrWhiteSpace(currentState))
                context.Append("Current state: ").Append(currentState.Trim()).AppendLine();
            AppendRecentMemory(context, profile, Mathf.Max(2, memoryLines));
            AppendSocialMemory(context, profile, Mathf.Max(2, memoryLines));
            AppendWorldEvents(context, 3);

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Plan the next " + requestedCount + " believable physical office activities for a simulation worker. You are " + who + ". " +
                    profile.personality + " Create a short, varied sequence that fits their needs, personality, and recent context. " +
                    "When practical, honor an unresolved promise, favor, invitation, or plan from social memory. " +
                    "Do not repeat the same action consecutively or fill the sequence with desk work. " +
                    "When one objective needs multiple physical steps, make 2 to 4 consecutive activities a linked sequence. " +
                    "Give those activities the same short sequenceId and objective, with sequenceStep starting at 1 and increasing by 1. " +
                    "Example: walk to printer, use printer, then return to desk. Leave sequenceId and objective empty and sequenceStep 0 for independent activities. " +
                    "ActionPoint means an exact object such as a desk, printer, or coffee machine. " +
                    "FreePosition means a reachable place in the office. CurrentPosition means no travel. " +
                    "FollowAgent means approach the named coworker's live position; use it only for ApproachColleague. " +
                    "You may use Custom as actionType to invent a new custom action not tied to any scene object; set customActionLabel to a short description (e.g., \"stretch arms\", \"look out window\"). " +
                    "Custom actions cannot create, fetch, carry, give, or transfer objects. Do not plan snacks, food, presents, birthday gifts, or other unavailable items unless the action is VendingMachine. " +
                    "Custom actions use FreePosition or CurrentPosition as destinationMode. " +
                    "Optionally set energyChange, focusChange, socialChange, productivityChange (negative to decrease, positive to increase, default 0) to describe need effects. " +
                    "If an activity physically fulfills one open social-memory commitment, copy that memory's subject exactly into socialMemorySubject; otherwise use an empty string. " +
                    "For an invitation, choose BreakSpot or ChatSpot, target the invited coworker, and write a specific in-character socialOpeningLine that refers naturally to the remembered plan. " +
                    "socialOpeningLine must use simple everyday English and contain one sentence of 4 to 12 words with one clear idea. " +
                    "Respond only as JSON with exactly one activities array: " +
                    "{\"activities\":[{\"actionType\":string,\"destinationMode\":string,\"destinationHint\":string," +
                    "\"sequenceId\":string,\"sequenceStep\":number,\"objective\":string," +
                    "\"targetAgent\":string,\"durationSeconds\":number,\"reason\":string,\"thought\":string," +
                    "\"customActionLabel\":string,\"energyChange\":number,\"focusChange\":number,\"socialChange\":number,\"productivityChange\":number,\"socialMemorySubject\":string,\"socialOpeningLine\":string}" +
                    "]}. Return exactly " + requestedCount + " activity objects. " +
                    "actionType must be one of: " + allowed + ", or Custom. " +
                    "destinationMode must be ActionPoint, FreePosition, CurrentPosition, or FollowAgent. " +
                    "For a free destination, destinationHint can be General, Quiet, Lounge, WorkArea, or Corridor. " +
                    (string.IsNullOrWhiteSpace(coworkerNames)
                        ? "No coworkers are currently available, so do not choose ApproachColleague. "
                        : "For ApproachColleague, targetAgent must be exactly one of: " + coworkerNames + ". ") +
                    "Use ApproachColleague when asking that coworker for advice, help, or a discussion. For ApproachColleague, thought must be the exact short sentence spoken on arrival, not an internal thought. " +
                    "durationSeconds must be between 2 and 30. " +
                    "reason is 3 to 14 words. thought is an optional short in-character thought, 0 to 12 words."),
                new ChatMessage("user", context.ToString())
            };

            LLMOptions options = new()
            {
                requestLabel = "ActivityBatch:" + who,
                temperature = Mathf.Clamp(temperature, 0.55f, 0.8f),
                maxTokens = Mathf.Clamp(requestedCount * 150, 300, 800),
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ActivityPlan,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds,
                    Mathf.Max(8, activityPlanTimeoutSeconds)),
                maxRetries = 2,
                retryBaseDelaySeconds = 5f
            };
            string raw = await backend.CompleteAsync(messages, options);
            List<OfficeActivityPlan> plans = ParseActivityPlans(
                raw, availableActions, coworkers, requestedCount, profile);
            plans = PrependCommitment(plans, commitmentPlan, requestedCount);
            if (commitmentPlan != null && plans != null && plans.Count > 0)
            {
                OfficeActivityPlan selectedCommitment = plans[0];
                bool modelSelected = !ReferenceEquals(selectedCommitment, commitmentPlan);
                Debug.Log("[Social commitment plan] " + who + ": " +
                    (modelSelected ? "model chose " : "fallback chose ") +
                    selectedCommitment.actionType +
                    (string.IsNullOrWhiteSpace(selectedCommitment.targetAgent)
                        ? "" : " with " + selectedCommitment.targetAgent), this);
            }
            if ((plans == null || plans.Count == 0) && !string.IsNullOrWhiteSpace(raw))
                Debug.LogWarning("[Activity batch rejected] " + who +
                    ": no valid available activities", this);
            else if (plans != null && plans.Count > 0)
                Debug.Log("[Activity batch] " + who + ": accepted " + plans.Count +
                    " queued activities", this);
            return plans;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " activity planning failed for " +
                agentId + ": " + exception.Message, this);
            return SinglePlan(commitmentPlan);
        }
        finally
        {
            socialRequestGate.Release();
        }
    }

    private static List<OfficeActivityPlan> SinglePlan(OfficeActivityPlan plan)
    {
        return plan == null ? null : new List<OfficeActivityPlan> { plan };
    }

    private static List<OfficeActivityPlan> PrependCommitment(List<OfficeActivityPlan> plans,
        OfficeActivityPlan commitment, int capacity)
    {
        if (commitment == null)
            return plans;
        plans ??= new List<OfficeActivityPlan>();
        for (int i = 0; i < plans.Count; i++)
        {
            if (plans[i] == null || string.IsNullOrWhiteSpace(plans[i].socialMemorySubject))
                continue;
            OfficeActivityPlan generatedCommitment = plans[i];
            plans.RemoveAt(i);
            plans.Insert(0, generatedCommitment);
            while (plans.Count > Mathf.Max(1, capacity))
                plans.RemoveAt(plans.Count - 1);
            return plans;
        }
        plans.Insert(0, commitment);
        while (plans.Count > Mathf.Max(1, capacity))
            plans.RemoveAt(plans.Count - 1);
        return plans;
    }

    private static OfficeActivityPlan BuildOpenCommitmentPlan(AgentProfile profile,
        List<OfficeActionType> availableActions, List<ConversationParticipantContext> coworkers)
    {
        string actor = DisplayName(profile, profile.agentId);
        for (int i = profile.socialMemory.Count - 1; i >= 0; i--)
        {
            SocialMemoryEntry entry = profile.socialMemory[i];
            if (entry == null || entry.status != "open")
                continue;

            bool actorResponsible = string.Equals(entry.sourceAgent, actor,
                StringComparison.OrdinalIgnoreCase);
            if (entry.type == "favor_request" && !string.IsNullOrWhiteSpace(entry.targetAgent))
                actorResponsible = string.Equals(entry.targetAgent, actor,
                    StringComparison.OrdinalIgnoreCase);
            if (!actorResponsible)
                continue;

            if (entry.type == "invitation")
            {
                ConversationParticipantContext target = FindParticipant(coworkers, entry.targetAgent);
                if (target == null)
                    continue;
                OfficeActionType? socialType = availableActions.Contains(OfficeActionType.BreakSpot)
                    ? OfficeActionType.BreakSpot
                    : availableActions.Contains(OfficeActionType.ChatSpot)
                        ? OfficeActionType.ChatSpot : null;
                if (!socialType.HasValue)
                    continue;
                return new OfficeActivityPlan
                {
                    actionType = socialType.Value,
                    destinationMode = OfficeDestinationMode.ActionPoint,
                    targetAgent = target.displayName,
                    durationSeconds = 8f,
                    reason = "follow through on the invitation",
                    thought = "I should keep that invitation.",
                    socialMemorySubject = entry.subject,
                    completesSocialCommitment = true
                };
            }

            if (entry.type == "promise" || entry.type == "favor_request" || entry.type == "plan")
            {
                bool coffeeDelivery = ContainsIgnoreCase(entry.subject, "coffee")
                    || ContainsIgnoreCase(entry.subject, "drink");
                if (coffeeDelivery)
                {
                    ConversationParticipantContext target = FindParticipant(coworkers, entry.targetAgent);
                    if (target == null)
                        continue;
                    AIWorkerAgent actorAgent = FindLiveAgent(actor);
                    if (actorAgent != null && actorAgent.IsHolding)
                    {
                        return new OfficeActivityPlan
                        {
                            actionType = OfficeActionType.ApproachColleague,
                            destinationMode = OfficeDestinationMode.FollowAgent,
                            targetAgent = target.displayName,
                            durationSeconds = 2f,
                            reason = "deliver the promised coffee",
                            thought = "I should deliver this coffee.",
                            socialMemorySubject = entry.subject,
                            completesSocialCommitment = true
                        };
                    }
                    if (!availableActions.Contains(OfficeActionType.CoffeeMachine))
                        continue;
                    return new OfficeActivityPlan
                    {
                        actionType = OfficeActionType.CoffeeMachine,
                        destinationMode = OfficeDestinationMode.ActionPoint,
                        durationSeconds = 3f,
                        reason = "get the promised coffee",
                        thought = "I promised to bring coffee.",
                        socialMemorySubject = entry.subject,
                        completesSocialCommitment = false
                    };
                }
                if (ContainsUnavailableObjectClaim(entry.subject))
                    continue;
                string counterpartName = string.Equals(entry.sourceAgent, actor,
                        StringComparison.OrdinalIgnoreCase)
                    ? entry.targetAgent : entry.sourceAgent;
                ConversationParticipantContext counterpart = FindParticipant(coworkers, counterpartName);
                if (counterpart == null || !availableActions.Contains(OfficeActionType.ApproachColleague))
                    continue;
                return new OfficeActivityPlan
                {
                    actionType = OfficeActionType.ApproachColleague,
                    destinationMode = OfficeDestinationMode.FollowAgent,
                    targetAgent = counterpart.displayName,
                    durationSeconds = 5f,
                    reason = entry.subject,
                    thought = counterpart.displayName + ", can we handle what we discussed?",
                    socialChange = 2f,
                    socialMemorySubject = entry.subject,
                    completesSocialCommitment = true
                };
            }
        }
        return null;
    }

    private static AIWorkerAgent FindLiveAgent(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (AIWorkerAgent agent in FindObjectsByType<AIWorkerAgent>(FindObjectsSortMode.None))
            if (agent != null && string.Equals(agent.DisplayName, displayName,
                    StringComparison.OrdinalIgnoreCase))
                return agent;
        return null;
    }

    public async Task<ConversationScript> GenerateConversationAsync(
        List<ConversationParticipantContext> participants,
        string openingSpeaker,
        string openingLine,
        string topic,
        List<string> speakerOrder)
    {
        if (backend == null || participants == null || participants.Count < 2
            || speakerOrder == null || speakerOrder.Count == 0)
            return null;

        Interlocked.Increment(ref pendingConversationScripts);
        await socialRequestGate.WaitAsync();
        try
        {
            string names = JoinParticipantNames(participants);
            StringBuilder cast = new();
            cast.AppendLine("Characters:");
            foreach (ConversationParticipantContext participant in participants)
            {
                if (participant == null || string.IsNullOrWhiteSpace(participant.displayName))
                    continue;
                AgentProfile profile = GetProfile(participant.agentId);
                cast.Append("- ").Append(participant.displayName).Append(": ")
                    .Append(profile != null ? profile.personality : "office coworker");
                if (!string.IsNullOrWhiteSpace(participant.relationships))
                    cast.Append(" Current relationships: ").Append(participant.relationships.Trim());
                cast.AppendLine();
                if (profile != null)
                {
                    AppendRecentMemory(cast, profile, 1, participant.displayName);
                    AppendSocialMemory(cast, profile, 2, participant.displayName);
                    AppendRecentList(cast, participant.displayName +
                        "'s recent phrases; do not repeat them:",
                        profile.recentUtterances, 2);
                }
            }
            AppendWorldEvents(cast, 2);
            AppendRecentList(cast, "Recent lines heard anywhere in the office; do not reuse or lightly paraphrase:",
                recentGlobalUtterances, 8);
            AppendRecentList(cast, "Recent office conversation subjects; choose a different angle:",
                recentGlobalTopics, 6);

            StringBuilder order = new();
            for (int i = 0; i < speakerOrder.Count; i++)
            {
                order.Append("reply").Append(i + 1).Append(" is spoken by ")
                    .Append(speakerOrder[i]).AppendLine(".");
            }
            string replyKeys = BuildReplyKeyList(speakerOrder.Count);

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Continue one natural face-to-face workplace conversation. Each reply value is spoken by the assigned " +
                    "character. This short exchange has one subject only. Do not introduce a new topic, story, plan, invitation, " +
                    "or unrelated question. Each line must directly react to the immediately previous line and must mention either " +
                    "the established subject or one concrete detail from that previous line. Keep the same people and facts, and " +
                    "sound distinctively like that character. Use at least one specific profile detail, interest, relationship, " +
                    "memory, job detail, or speech-style cue across the replies; avoid reusable workplace filler. " +
                    "Use simple everyday spoken English and contractions. Let people answer, " +
                    "add a concrete detail, tease, hesitate, or disagree when appropriate; they must not merely praise or agree. " +
                    "Each line must express one clear idea in one sentence. Prefer common words a child or new English learner can understand. " +
                    "Avoid metaphors, abstract advice, corporate language, semicolons, and long explanations. " +
                    "Characters may offer to bring coffee, because they can physically fetch and deliver it. A vending machine can dispense a snack for the actor to hold locally. Do not offer snacks, food, gifts, or other unavailable objects unless the action is VendingMachine. " +
                    "Do not claim someone brought, carries, owns, gives, or received an object unless the opening line explicitly establishes that physical object. " +
                    "A private social memory belongs only to the named character until they choose to say it aloud. " +
                    "Every reply value must be non-empty and use 3 to 12 words. Do not invent shared history, switch roles, narrate actions, use speaker labels " +
                    "inside a line, mention AI, or repeat wording. Do not end every line with a question. " +
                    "If the opening already congratulated someone, do not repeat the exact phrase happy birthday; add a personal wish, thanks, joke, or gift reaction instead. " +
                    "Also extract up to two explicit social events stated in the conversation. Allowed event types are secret, gossip, promise, favor_request, favor_done, plan, and invitation. " +
                    "Do not infer an event from ordinary chat. For each event, speaker and target must be character names, subject must be a short concrete fact or commitment, and private is true only when it was presented as confidential. " +
                    "Return an empty socialEvents array when there is no explicit event. Return only JSON with these reply keys: " + replyKeys +
                    ", plus socialEvents:[{type:string,speaker:string,target:string,subject:string,private:bool}]."),
                new ChatMessage("user",
                    cast + "Conversation fact: " + openingSpeaker + " initiated this subject and said the opening line. " +
                    "Do not transfer " + openingSpeaker + "'s actions or memories to somebody else.\n" +
                    "Established subject: " + topic + "\n" + openingSpeaker + " said: \"" + openingLine +
                    "\"\nReply assignments:\n" + order)
            };

            LLMOptions options = new()
            {
                requestLabel = "ConversationScript",
                temperature = Mathf.Clamp(temperature, 0.55f, 0.72f),
                maxTokens = Mathf.Clamp(56 * speakerOrder.Count + 100, 220, 440),
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ConversationScript,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds, conversationScriptTimeoutSeconds)
            };
            string raw = await backend.CompleteAsync(messages, options);
            ConversationScript script = ParseAndValidateConversationScript(raw, participants,
                speakerOrder, openingSpeaker, openingLine, topic, names, out string rejection);
            if (!string.IsNullOrWhiteSpace(rejection))
                Debug.LogWarning("[Conversation script " + (script == null ? "rejected" : "partial")
                    + "] " + names + ": " + rejection, this);
            return script;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " conversation generation failed: " +
                exception.Message, this);
            return null;
        }
        finally
        {
            socialRequestGate.Release();
            Interlocked.Decrement(ref pendingConversationScripts);
        }
    }

    public bool IsFreshOpening(string agentId, string line)
    {
        AgentProfile profile = GetProfile(agentId);
        return profile == null || !IsSimilarToAny(line, profile.recentOpenings, 0.72f);
    }

    public void RecordConversation(List<ConversationParticipantContext> participants,
        string topic, string openingSpeaker, string openingLine, List<ConversationTurn> turns,
        List<SocialMemoryEntry> socialEvents = null)
    {
        if (participants == null)
            return;

        foreach (ConversationParticipantContext participant in participants)
        {
            AgentProfile profile = participant != null ? GetProfile(participant.agentId) : null;
            if (profile == null)
                continue;
            AddRecent(profile.recentTopics, topic, 8);
            if (string.Equals(participant.displayName, openingSpeaker, StringComparison.OrdinalIgnoreCase))
            {
                AddRecent(profile.recentOpenings, openingLine, 8);
                AddRecent(profile.recentUtterances, openingLine, 10);
            }
        }

        AddRecent(recentGlobalTopics, topic, 12);
        AddRecent(recentGlobalUtterances, openingLine, 24);

        if (turns != null)
        {
            foreach (ConversationTurn turn in turns)
            {
                ConversationParticipantContext participant = FindParticipant(participants, turn?.speaker);
                AgentProfile profile = participant != null ? GetProfile(participant.agentId) : null;
                if (profile != null)
                    AddRecent(profile.recentUtterances, turn.line, 10);
                AddRecent(recentGlobalUtterances, turn.line, 24);
            }
        }

        socialEvents ??= new List<SocialMemoryEntry>();
        AddConcreteOfferEvents(participants, openingSpeaker, openingLine, turns, socialEvents);
        RecordSocialEvents(participants, socialEvents);
    }

    private static void AddConcreteOfferEvents(List<ConversationParticipantContext> participants,
        string openingSpeaker, string openingLine, List<ConversationTurn> turns,
        List<SocialMemoryEntry> socialEvents)
    {
        if (socialEvents == null)
            return;
        string previousSpeaker = "";
        AddConcreteOfferEvent(participants, openingSpeaker, openingLine, previousSpeaker, socialEvents);
        previousSpeaker = openingSpeaker;
        if (turns == null)
            return;
        foreach (ConversationTurn turn in turns)
        {
            AddConcreteOfferEvent(participants, turn?.speaker, turn?.line, previousSpeaker, socialEvents);
            if (turn != null && !string.IsNullOrWhiteSpace(turn.speaker))
                previousSpeaker = turn.speaker;
        }
    }

    private static void AddConcreteOfferEvent(List<ConversationParticipantContext> participants,
        string speaker, string line, string previousSpeaker, List<SocialMemoryEntry> socialEvents)
    {
        bool coffeeOffer = ContainsIgnoreCase(line, "coffee")
            && ContainsIgnoreCase(line, "you")
            && (ContainsIgnoreCase(line, "bring") || ContainsIgnoreCase(line, "get")
                || ContainsIgnoreCase(line, "fetch") || ContainsIgnoreCase(line, "grab"));
        if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(line) || !coffeeOffer)
            return;
        string target = previousSpeaker;
        foreach (ConversationParticipantContext participant in participants)
            if (participant != null && !string.Equals(participant.displayName, speaker,
                    StringComparison.OrdinalIgnoreCase)
                && ContainsIgnoreCase(line, participant.displayName))
            {
                target = participant.displayName;
                break;
            }
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, speaker,
                StringComparison.OrdinalIgnoreCase))
            return;
        socialEvents.Add(new SocialMemoryEntry
        {
            type = "promise",
            sourceAgent = speaker,
            targetAgent = target,
            subject = speaker + " promised to bring coffee to " + target + ".",
            status = "open"
        });
    }

    private void RecordSocialEvents(List<ConversationParticipantContext> participants,
        List<SocialMemoryEntry> socialEvents)
    {
        if (participants == null || socialEvents == null || socialEvents.Count == 0)
            return;

        foreach (SocialMemoryEntry socialEvent in socialEvents)
        {
            if (socialEvent == null)
                continue;
            int listenerCount = 0;
            foreach (ConversationParticipantContext participant in participants)
            {
                AgentProfile profile = participant != null ? GetProfile(participant.agentId) : null;
                if (profile == null || HasSocialMemory(profile, socialEvent))
                    continue;

                ResolveCompletedFavor(profile, socialEvent);
                profile.socialMemory.Add(new SocialMemoryEntry
                {
                    type = socialEvent.type,
                    sourceAgent = socialEvent.sourceAgent,
                    targetAgent = socialEvent.targetAgent,
                    subject = socialEvent.subject,
                    isPrivate = socialEvent.isPrivate,
                    status = socialEvent.status
                });
                while (profile.socialMemory.Count > 20)
                    profile.socialMemory.RemoveAt(0);
                listenerCount++;
            }
            if (listenerCount > 0)
                Debug.Log("[Social memory] " + FormatSocialMemory(socialEvent) +
                    " (heard by " + listenerCount + ")", this);
        }
    }

    private static bool HasSocialMemory(AgentProfile profile, SocialMemoryEntry candidate)
    {
        int start = Mathf.Max(0, profile.socialMemory.Count - 8);
        for (int i = start; i < profile.socialMemory.Count; i++)
        {
            SocialMemoryEntry previous = profile.socialMemory[i];
            if (previous != null
                && string.Equals(previous.type, candidate.type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.sourceAgent, candidate.sourceAgent, StringComparison.OrdinalIgnoreCase)
                && TextSimilarity(previous.subject, candidate.subject) >= 0.75f)
                return true;
        }
        return false;
    }

    private static void ResolveCompletedFavor(AgentProfile profile, SocialMemoryEntry completed)
    {
        if (!string.Equals(completed.type, "favor_done", StringComparison.OrdinalIgnoreCase))
            return;
        for (int i = profile.socialMemory.Count - 1; i >= 0; i--)
        {
            SocialMemoryEntry previous = profile.socialMemory[i];
            if (previous == null || previous.status != "open"
                || !string.Equals(previous.type, "favor_request", StringComparison.OrdinalIgnoreCase))
                continue;
            bool samePeople = string.Equals(previous.sourceAgent, completed.targetAgent,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(previous.targetAgent, completed.sourceAgent,
                    StringComparison.OrdinalIgnoreCase);
            if (samePeople && TextSimilarity(previous.subject, completed.subject) >= 0.25f)
            {
                previous.status = "completed";
                return;
            }
        }
    }

    private void AppendRecentMemory(StringBuilder target, AgentProfile profile, int maxLines,
        string ownerName = null)
    {
        if (profile == null || profile.memory.Count == 0)
            return;
        target.AppendLine(string.IsNullOrWhiteSpace(ownerName)
            ? "Your recent personal memories:"
            : ownerName.Trim() + "'s recent memories:");
        int start = Mathf.Max(0, profile.memory.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < profile.memory.Count; i++)
            target.Append("- ").Append(profile.memory[i]).AppendLine();
    }

    private static void AppendSocialMemory(StringBuilder target, AgentProfile profile, int maxLines,
        string ownerName = null)
    {
        if (profile == null || profile.socialMemory.Count == 0)
            return;
        target.AppendLine(string.IsNullOrWhiteSpace(ownerName)
            ? "Your social knowledge and commitments:"
            : ownerName.Trim() + "'s social knowledge and commitments:");
        int start = Mathf.Max(0, profile.socialMemory.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < profile.socialMemory.Count; i++)
            target.Append("- ").Append(FormatSocialMemory(profile.socialMemory[i])).AppendLine();
    }

    private static string FormatSocialMemory(SocialMemoryEntry entry)
    {
        if (entry == null)
            return "<invalid>";
        string privacy = entry.isPrivate ? "private, " : "";
        string target = string.IsNullOrWhiteSpace(entry.targetAgent)
            ? "" : " -> " + entry.targetAgent.Trim();
        return "[" + privacy + entry.type + ", " + entry.status + "] " +
            entry.sourceAgent + target + ": " + entry.subject;
    }

    private void AppendWorldEvents(StringBuilder target, int maxLines = 4)
    {
        if (worldEvents.Count == 0)
            return;
        target.AppendLine("Recent shared office events:");
        int start = Mathf.Max(0, worldEvents.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < worldEvents.Count; i++)
            target.Append("- ").Append(worldEvents[i]).AppendLine();
    }

    private static void AppendRecentList(StringBuilder target, string heading,
        List<string> values, int maxLines)
    {
        if (target == null || values == null || values.Count == 0)
            return;
        target.AppendLine(heading);
        int start = Mathf.Max(0, values.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < values.Count; i++)
            target.Append("- ").Append(values[i]).AppendLine();
    }

    private static string DisplayName(AgentProfile profile, string fallback)
    {
        return profile != null && !string.IsNullOrWhiteSpace(profile.displayName)
            ? profile.displayName : fallback;
    }

    private static string JoinParticipantNames(List<ConversationParticipantContext> participants)
    {
        StringBuilder result = new();
        if (participants == null)
            return "";
        foreach (ConversationParticipantContext participant in participants)
        {
            if (participant == null || string.IsNullOrWhiteSpace(participant.displayName))
                continue;
            if (result.Length > 0)
                result.Append(", ");
            result.Append(participant.displayName.Trim());
        }
        return result.ToString();
    }

    private static string JoinActionTypes(List<OfficeActionType> actions)
    {
        StringBuilder result = new();
        foreach (OfficeActionType action in actions)
        {
            if (result.Length > 0)
                result.Append(", ");
            result.Append(action);
        }
        return result.ToString();
    }

    private List<OfficeActivityPlan> ParseActivityPlans(
        string raw,
        List<OfficeActionType> availableActions,
        List<ConversationParticipantContext> coworkers,
        int maxPlans,
        AgentProfile profile)
    {
        if (CountOccurrences(raw, "\"activities\"") != 1)
            return null;
        string json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            ActivityPlanBatchDTO batch = JsonUtility.FromJson<ActivityPlanBatchDTO>(json);
            if (batch == null || batch.activities == null || batch.activities.Length == 0)
                return null;

            List<OfficeActivityPlan> result = new();
            OfficeActionType? previousType = null;
            int count = Mathf.Min(Mathf.Max(1, maxPlans), batch.activities.Length);
            for (int i = 0; i < count; i++)
            {
                OfficeActivityPlan plan = ParseActivityPlanItem(
                    batch.activities[i], availableActions, coworkers, profile);
                if (plan == null || previousType == plan.actionType)
                    continue;

                result.Add(plan);
                previousType = plan.actionType;
            }
            NormalizeActivitySequences(result);
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private OfficeActivityPlan ParseActivityPlanItem(
        ActivityPlanDTO dto,
        List<OfficeActionType> availableActions,
        List<ConversationParticipantContext> coworkers,
        AgentProfile profile)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.actionType))
            return null;

        OfficeActionType actionType;
        bool isCustom = string.Equals(dto.actionType.Trim(), "Custom", StringComparison.OrdinalIgnoreCase);
        if (!isCustom)
        {
            if (!Enum.TryParse(dto.actionType.Trim(), true, out actionType)
                || availableActions == null || !availableActions.Contains(actionType))
                return null;
        }
        else
        {
            actionType = OfficeActionType.Custom;
            string customDescription = (dto.customActionLabel ?? "") + " "
                + (dto.reason ?? "") + " " + (dto.thought ?? "");
            if (ContainsUnavailableObjectClaim(customDescription))
            {
                Debug.LogWarning("[Activity rejected] " + DisplayName(profile, profile.agentId)
                    + ": unavailable object action: " + customDescription.Trim(), this);
                return null;
            }
        }

        OfficeDestinationMode destinationMode;
        if (isCustom)
        {
            destinationMode = !string.IsNullOrWhiteSpace(dto.destinationMode)
                && Enum.TryParse(dto.destinationMode.Trim(), true, out OfficeDestinationMode parsedMode)
                ? parsedMode
                : OfficeDestinationMode.FreePosition;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(dto.destinationMode)
                || !Enum.TryParse(dto.destinationMode.Trim(), true, out destinationMode))
                return null;
        }

        string targetAgent = CleanShortText(dto.targetAgent, 6);
        bool requiresCoworker = destinationMode == OfficeDestinationMode.FollowAgent
            || actionType == OfficeActionType.ApproachColleague;
        if (requiresCoworker && FindParticipant(coworkers, targetAgent) == null)
            return null;

        SocialMemoryEntry commitment = FindOpenSocialMemory(profile, dto.socialMemorySubject);
        bool socialAction = actionType == OfficeActionType.BreakSpot
            || actionType == OfficeActionType.ChatSpot;
        if (commitment != null && commitment.type == "invitation"
            && (!socialAction || destinationMode != OfficeDestinationMode.ActionPoint
                || !string.Equals(targetAgent, commitment.targetAgent,
                    StringComparison.OrdinalIgnoreCase)))
            commitment = null;
        if (commitment != null && commitment.type != "invitation")
        {
            string actor = DisplayName(profile, profile.agentId);
            string counterpartName = string.Equals(commitment.sourceAgent, actor,
                    StringComparison.OrdinalIgnoreCase)
                ? commitment.targetAgent : commitment.sourceAgent;
            bool validInteraction = !ContainsUnavailableObjectClaim(commitment.subject)
                && actionType == OfficeActionType.ApproachColleague
                && destinationMode == OfficeDestinationMode.FollowAgent
                && string.Equals(targetAgent, counterpartName,
                    StringComparison.OrdinalIgnoreCase);
            if (!validInteraction)
            {
                Debug.LogWarning("[Commitment action rejected] "
                    + DisplayName(profile, profile.agentId) + ": action did not physically fulfill: "
                    + commitment.subject, this);
                commitment = null;
            }
        }

        return new OfficeActivityPlan
        {
            actionType = actionType,
            destinationMode = destinationMode,
            destinationHint = CleanShortText(dto.destinationHint, 3),
            sequenceId = CleanShortText(dto.sequenceId, 4),
            sequenceStep = Mathf.Max(0, dto.sequenceStep),
            objective = CleanShortText(dto.objective, 12),
            targetAgent = targetAgent,
            durationSeconds = Mathf.Clamp(dto.durationSeconds <= 0f ? 5f : dto.durationSeconds, 2f, 30f),
            reason = CleanShortText(dto.reason, 14),
            thought = CleanShortText(dto.thought, 12),
            customActionLabel = CleanShortText(dto.customActionLabel, 24),
            energyChange = dto.energyChange,
            focusChange = dto.focusChange,
            socialChange = dto.socialChange,
            productivityChange = dto.productivityChange,
            socialMemorySubject = commitment != null ? commitment.subject : "",
            completesSocialCommitment = commitment != null,
            socialOpeningLine = commitment != null && socialAction
                ? CleanShortText(dto.socialOpeningLine, 20) : ""
        };
    }

    private static void NormalizeActivitySequences(List<OfficeActivityPlan> plans)
    {
        if (plans == null)
            return;

        int index = 0;
        while (index < plans.Count)
        {
            OfficeActivityPlan first = plans[index];
            if (first == null || string.IsNullOrWhiteSpace(first.sequenceId))
            {
                ClearSequence(first);
                index++;
                continue;
            }

            int end = index + 1;
            while (end < plans.Count && plans[end] != null
                && string.Equals(plans[end].sequenceId, first.sequenceId,
                    StringComparison.OrdinalIgnoreCase))
                end++;

            bool valid = end - index >= 2 && !string.IsNullOrWhiteSpace(first.objective);
            for (int i = index; valid && i < end; i++)
            {
                valid = plans[i].sequenceStep == i - index + 1
                    && string.Equals(plans[i].objective, first.objective,
                        StringComparison.OrdinalIgnoreCase);
            }

            if (!valid)
                for (int i = index; i < end; i++)
                    ClearSequence(plans[i]);
            index = end;
        }
    }

    private static void ClearSequence(OfficeActivityPlan plan)
    {
        if (plan == null)
            return;
        plan.sequenceId = "";
        plan.sequenceStep = 0;
        plan.objective = "";
    }

    private static SocialMemoryEntry FindOpenSocialMemory(AgentProfile profile, string candidate)
    {
        if (profile == null || string.IsNullOrWhiteSpace(candidate))
            return null;
        for (int i = profile.socialMemory.Count - 1; i >= 0; i--)
        {
            SocialMemoryEntry entry = profile.socialMemory[i];
            if (entry != null && entry.status == "open"
                && TextSimilarity(entry.subject, candidate) >= 0.7f)
                return entry;
        }
        return null;
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
                || TextSimilarity(entry.subject, subject) < 0.7f)
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
                    && TextSimilarity(entry.subject, completed.subject) >= 0.7f)
                    entry.status = "completed";

        Debug.Log("[Social memory completed] " + DisplayName(profile, agentId) + ": " +
            FormatSocialMemory(completed), this);
    }

    private ConversationPlan ParseConversationPlan(string raw)
    {
        if (CountOccurrences(raw, "\"targetAgent\"") != 1
            || CountOccurrences(raw, "\"topic\"") != 1
            || CountOccurrences(raw, "\"openingLine\"") != 1)
            return null;
        string json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            ConversationPlanDTO dto = JsonUtility.FromJson<ConversationPlanDTO>(json);
            if (dto == null)
                return null;
            return new ConversationPlan
            {
                targetAgent = dto.targetAgent,
                topic = dto.topic,
                openingLine = dto.openingLine
            };
        }
        catch
        {
            return null;
        }
    }

    private string ValidateConversationPlan(ConversationPlan plan, AgentProfile profile,
        List<ConversationParticipantContext> coworkers, string speakerName, string participantNames,
        out ConversationPlan validated)
    {
        validated = null;
        if (plan == null)
            return "response was not valid conversation-plan JSON";

        ConversationParticipantContext target = FindParticipant(coworkers, plan.targetAgent);
        if (target == null)
            return "targetAgent was not one of the available coworkers";

        string topic = CleanTopic(plan.topic);
        if (string.IsNullOrWhiteSpace(topic))
            return "topic was empty or too vague";
        if (IsGenericTopic(topic))
            return "topic was a generic conversation label instead of a concrete subject";
        if (LooksLikePersonName(topic, coworkers))
            return "topic was just a person's name instead of a concrete subject";
        if (IsSimilarToAny(topic, profile.recentTopics, 0.7f))
            return "topic repeated a recent subject";

        string opening = CleanReply(plan.openingLine, speakerName, participantNames,
            out string rejection);
        if (string.IsNullOrWhiteSpace(opening))
            return "opening line was unusable: " + rejection;
        if (opening.IndexOf(speakerName, StringComparison.OrdinalIgnoreCase) >= 0)
            return "opening addressed the initiating character instead of the coworker";
        if (IsGenericOpening(opening))
            return "opening was a generic check-in";
        if (IsSimilarToAny(opening, profile.recentOpenings, 0.72f))
            return "opening line repeated a recent opening";

        int wordCount = CountWords(opening);
        if (wordCount < 4 || wordCount > 24)
            return "opening line had " + wordCount + " words instead of 4-24";

        validated = new ConversationPlan
        {
            targetAgent = target.displayName,
            topic = topic,
            openingLine = opening
        };
        return null;
    }

    private ConversationScript ParseAndValidateConversationScript(string raw,
        List<ConversationParticipantContext> participants, List<string> speakerOrder,
        string openingSpeaker, string openingLine, string topic, string participantNames,
        out string rejection)
    {
        rejection = null;
        string json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            rejection = "empty or non-JSON response";
            return null;
        }

        ConversationScriptDTO script;
        try
        {
            script = JsonUtility.FromJson<ConversationScriptDTO>(json);
        }
        catch
        {
            rejection = "invalid JSON";
            return null;
        }

        if (script == null)
        {
            rejection = "script contained no turns";
            return null;
        }

        List<ConversationTurn> accepted = new();
        ConversationThreadState thread = new(topic, openingLine);
        List<string> lines = new() { openingLine };
        string[] generatedLines =
        {
            script.reply1,
            script.reply2,
            script.reply3,
            script.reply4,
            script.reply5,
            script.reply6
        };
        int count = Mathf.Min(generatedLines.Length, speakerOrder.Count);
        for (int i = 0; i < count; i++)
        {
            string expectedSpeaker = speakerOrder[i];
            string line = CleanReply(generatedLines[i], expectedSpeaker, participantNames,
                out string lineRejection);
            ConversationParticipantContext participant = FindParticipant(participants, expectedSpeaker);
            AgentProfile profile = participant != null ? GetProfile(participant.agentId) : null;
            if (string.IsNullOrWhiteSpace(line))
            {
                rejection = "turn " + (i + 1) + " was unusable: " + lineRejection;
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }
            if (IsSimilarToAny(line, lines, 0.8f)
                || (profile != null && IsSimilarToAny(line, profile.recentUtterances, 0.78f)))
            {
                rejection = "turn " + (i + 1) + " repeated a recent line";
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }
            if (IsSimilarToAny(line, recentGlobalUtterances, 0.82f))
            {
                rejection = "turn " + (i + 1) + " repeated a line recently heard elsewhere";
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }
            if (!thread.TryAccept(line))
            {
                rejection = "turn " + (i + 1) + " changed to an unrelated subject";
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }

            accepted.Add(new ConversationTurn { speaker = expectedSpeaker, line = line });
            lines.Add(line);
        }

        if (accepted.Count == 0)
        {
            rejection = "script had no valid turns";
            return null;
        }
        return BuildConversationScript(accepted, script.socialEvents, participants,
            openingSpeaker, openingLine);
    }

    private static ConversationScript BuildConversationScript(List<ConversationTurn> turns,
        SocialEventDTO[] rawEvents, List<ConversationParticipantContext> participants,
        string openingSpeaker, string openingLine)
    {
        ConversationScript result = new();
        result.turns.AddRange(turns);
        if (rawEvents == null || rawEvents.Length == 0)
            return result;

        StringBuilder transcript = new();
        transcript.Append(openingSpeaker).Append(": ").Append(openingLine).Append(' ');
        foreach (ConversationTurn turn in turns)
            transcript.Append(turn.speaker).Append(": ").Append(turn.line).Append(' ');

        int count = Mathf.Min(2, rawEvents.Length);
        for (int i = 0; i < count; i++)
        {
            SocialMemoryEntry socialEvent = ValidateSocialEvent(rawEvents[i], participants,
                transcript.ToString());
            if (socialEvent != null)
                result.socialEvents.Add(socialEvent);
        }
        return result;
    }

    private static SocialMemoryEntry ValidateSocialEvent(SocialEventDTO raw,
        List<ConversationParticipantContext> participants, string transcript)
    {
        if (raw == null || string.IsNullOrWhiteSpace(raw.type)
            || string.IsNullOrWhiteSpace(raw.speaker) || string.IsNullOrWhiteSpace(raw.subject))
            return null;

        string type = raw.type.Trim().ToLowerInvariant();
        string[] allowedTypes =
        {
            "secret", "gossip", "promise", "favor_request", "favor_done", "plan", "invitation"
        };
        if (Array.IndexOf(allowedTypes, type) < 0)
            return null;

        ConversationParticipantContext source = FindParticipant(participants, raw.speaker);
        ConversationParticipantContext target = FindParticipant(participants, raw.target);
        if (source == null || (!string.IsNullOrWhiteSpace(raw.target) && target == null))
            return null;

        string subject = raw.subject.Replace('\r', ' ').Replace('\n', ' ').Trim();
        int words = CountWords(subject);
        if (words < 2 || words > 28 || !HasMeaningfulOverlap(subject, transcript))
            return null;

        bool remainsOpen = type == "promise" || type == "favor_request"
            || type == "plan" || type == "invitation";
        return new SocialMemoryEntry
        {
            type = type,
            sourceAgent = source.displayName,
            targetAgent = target != null ? target.displayName : "",
            subject = subject.TrimEnd('.', '!', '?') + ".",
            isPrivate = raw.@private,
            status = remainsOpen ? "open" : type == "favor_done" ? "completed" : "noted"
        };
    }

    private static bool HasMeaningfulOverlap(string subject, string transcript)
    {
        HashSet<string> subjectWords = MeaningfulWords(NormalizeForComparison(subject));
        HashSet<string> transcriptWords = MeaningfulWords(NormalizeForComparison(transcript));
        foreach (string word in subjectWords)
            if (transcriptWords.Contains(word))
                return true;
        return false;
    }

    private sealed class ConversationThreadState
    {
        private readonly HashSet<string> subjectAnchors;
        private HashSet<string> previousAnchors;

        public ConversationThreadState(string topic, string openingLine)
        {
            subjectAnchors = MeaningfulWords(NormalizeForComparison(
                (topic ?? "") + " " + (openingLine ?? "")));
            previousAnchors = MeaningfulWords(NormalizeForComparison(openingLine));
        }

        public bool TryAccept(string line)
        {
            string normalized = NormalizeForComparison(line);
            if (StartsNewSubject(normalized))
                return false;

            HashSet<string> lineAnchors = MeaningfulWords(normalized);
            bool anchored = lineAnchors.Overlaps(subjectAnchors)
                || lineAnchors.Overlaps(previousAnchors)
                || IsDirectReaction(normalized);
            if (!anchored)
                return false;

            previousAnchors = lineAnchors;
            return true;
        }

        private static bool StartsNewSubject(string line)
        {
            string[] starters =
            {
                "by the way", "speaking of something else", "on another topic",
                "anyway have you", "forget that"
            };
            foreach (string starter in starters)
                if (line.StartsWith(starter, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool IsDirectReaction(string line)
        {
            string[] reactions =
            {
                "that sounds", "tell me more", "why do you", "i agree", "i disagree",
                "you are right", "good idea", "bad idea", "what happened next",
                "yes", "no", "exactly", "maybe", "really", "thank you", "thanks"
            };
            foreach (string reaction in reactions)
                if (line.StartsWith(reaction, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    private static string BuildReplyKeyList(int count)
    {
        StringBuilder result = new();
        for (int i = 0; i < count; i++)
        {
            if (result.Length > 0)
                result.Append(", ");
            result.Append("reply").Append(i + 1);
        }
        return result.ToString();
    }

    private static string CleanTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return null;
        string value = topic.Replace('\r', ' ').Replace('\n', ' ').Trim()
            .Trim('"', '\'', '.', '!', '?', ':', ';');
        int words = CountWords(value);
        return words >= 2 && words <= 18 ? value : null;
    }

    private static string CleanShortText(string value, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim()
            .Trim('"', '\'', '.', '!', '?', ':', ';');
        string[] words = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "";
        if (words.Length <= maxWords)
            return clean;

        StringBuilder result = new();
        for (int i = 0; i < maxWords; i++)
        {
            if (result.Length > 0)
                result.Append(' ');
            result.Append(words[i]);
        }
        return result.ToString();
    }

    private static bool IsGenericTopic(string topic)
    {
        string value = NormalizeForComparison(topic);
        string[] generic =
        {
            "office conversation", "casual conversation", "general conversation",
            "daily life", "work life", "catching up", "how the day is going",
            "talking with coworkers", "friendly chat"
        };
        foreach (string phrase in generic)
            if (value == phrase || value.Contains(phrase))
                return true;
        return false;
    }

    private static bool IsGenericOpening(string opening)
    {
        string value = NormalizeForComparison(opening);
        string[] generic =
        {
            "how are you", "how is your day", "hows your day", "how are things",
            "what is up", "whats up", "got a minute", "do you have a minute",
            "want to chat", "can we chat", "how is work", "hows work",
            "how have you been", "what are you up to"
        };
        foreach (string phrase in generic)
            if (value.Contains(phrase))
                return true;
        return false;
    }

    private static bool LooksLikePersonName(string topic, List<ConversationParticipantContext> coworkers)
    {
        string value = NormalizeForComparison(topic);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] words = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1 && IsNameLikeToken(words[0]))
            return true;

        if (coworkers != null)
        {
            foreach (ConversationParticipantContext coworker in coworkers)
            {
                if (coworker == null || string.IsNullOrWhiteSpace(coworker.displayName))
                    continue;
                if (value == NormalizeForComparison(coworker.displayName))
                    return true;
            }
        }

        return false;
    }

    private static bool IsNameLikeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || value.Length > 20)
            return false;

        foreach (char c in value)
        {
            if (!char.IsLetter(c) && c != '\'' && c != '-')
                return false;
        }

        return true;
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string value = raw.Trim();
        int start = value.IndexOf('{');
        int end = value.LastIndexOf('}');
        return start >= 0 && end > start ? value.Substring(start, end - start + 1) : null;
    }

    private static int CountOccurrences(string value, string fragment)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(fragment))
            return 0;
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }
        return count;
    }

    private static bool ContainsIgnoreCase(string value, string fragment)
    {
        return !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(fragment)
            && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ContainsUnavailableObjectClaim(string value)
    {
        string[] unavailable =
        {
            "gift", "present", "snack", "food", "cake", "dumpling"
        };
        foreach (string word in unavailable)
            if (ContainsIgnoreCase(value, word))
                return true;
        return false;
    }

    private static ConversationParticipantContext FindParticipant(
        List<ConversationParticipantContext> participants, string displayName)
    {
        if (participants == null || string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (ConversationParticipantContext participant in participants)
            if (participant != null && string.Equals(participant.displayName, displayName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return participant;
        return null;
    }

    private static void AddRecent(List<string> values, string value, int capacity)
    {
        if (values == null || string.IsNullOrWhiteSpace(value))
            return;
        values.Add(value.Trim());
        while (values.Count > Mathf.Max(1, capacity))
            values.RemoveAt(0);
    }

    private static bool IsSimilarToAny(string candidate, List<string> previous, float threshold)
    {
        if (string.IsNullOrWhiteSpace(candidate) || previous == null)
            return false;
        foreach (string value in previous)
            if (TextSimilarity(candidate, value) >= threshold)
                return true;
        return false;
    }

    private static float TextSimilarity(string first, string second)
    {
        string a = NormalizeForComparison(first);
        string b = NormalizeForComparison(second);
        if (a.Length == 0 || b.Length == 0)
            return 0f;
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 1f;

        HashSet<string> aWords = MeaningfulWords(a);
        HashSet<string> bWords = MeaningfulWords(b);
        if (aWords.Count == 0 || bWords.Count == 0)
            return 0f;
        int overlap = 0;
        foreach (string word in aWords)
            if (bWords.Contains(word))
                overlap++;
        return overlap / (float)Mathf.Min(aWords.Count, bWords.Count);
    }

    private static string NormalizeForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        StringBuilder result = new();
        bool previousSpace = false;
        foreach (char character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(character);
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                result.Append(' ');
                previousSpace = true;
            }
        }
        return result.ToString().Trim();
    }

    private static HashSet<string> MeaningfulWords(string normalized)
    {
        HashSet<string> result = new();
        string[] ignored = { "the", "a", "an", "and", "or", "but", "i", "you", "we", "it",
            "is", "are", "was", "were", "to", "of", "in", "on", "for", "that", "this", "my",
            "your", "our", "with", "have", "has", "had", "do", "did", "what", "how" };
        HashSet<string> stopWords = new(ignored);
        foreach (string word in normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (word.Length > 1 && !stopWords.Contains(word))
                result.Add(StemWord(word));
        return result;
    }

    private static string StemWord(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
            return word.Substring(0, word.Length - 3);
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
            return word.Substring(0, word.Length - 2);
        if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal))
            return word.Substring(0, word.Length - 1);
        return word;
    }

    private static int CountWords(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? 0 : value.Split(
            new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string CleanReply(string raw, string speakerName, string participants,
        out string rejectionReason)
    {
        rejectionReason = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            rejectionReason = "empty response";
            return null;
        }

        string s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0)
                s = s.Substring(firstNewline + 1);
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                s = s.Substring(0, fence);
            s = s.Trim();
        }

        bool hasUnclosedQuote = HasUnclosedDialogueQuote(s);

        int firstQuote = s.IndexOf('"');
        int lastQuote = s.LastIndexOf('"');
        if (firstQuote >= 0 && lastQuote > firstQuote)
            s = s.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Trim();

        s = SelectSpokenLine(s);
        if (string.IsNullOrWhiteSpace(s))
        {
            rejectionReason = "no usable spoken line";
            return null;
        }

        s = StripSpeakerLabels(s, speakerName, participants);
        s = StripReplyLabel(s);
        s = s?.Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();
        s = RemoveGenericAgreementOpening(s);
        if (IsAssistantStyleReply(s) || IsNarratedReply(s, speakerName, participants))
        {
            rejectionReason = string.IsNullOrWhiteSpace(s)
                ? "only a generic agreement opener"
                : "assistant-style or narrated response";
            return null;
        }

        s = KeepCompleteThought(s, hasUnclosedQuote, out rejectionReason);

        if (string.IsNullOrWhiteSpace(s))
            return null;

        return s;
    }

    private static string SelectSpokenLine(string text)
    {
        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim().TrimStart('-', '*').Trim();
            line = StripReplyLabel(line);
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (IsAssistantStyleReply(line))
                return null;
            return line;
        }

        return null;
    }

    private static string StripSpeakerLabels(string line, string speakerName, string participants)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        List<string> names = new();
        if (!string.IsNullOrWhiteSpace(speakerName))
            names.Add(speakerName.Trim());
        if (!string.IsNullOrWhiteSpace(participants))
        {
            string[] participantNames = participants.Split(',');
            foreach (string participantName in participantNames)
                if (!string.IsNullOrWhiteSpace(participantName))
                    names.Add(participantName.Trim());
        }

        string result = line.Trim();
        bool changed;
        do
        {
            changed = false;
            foreach (string name in names)
            {
                string label = name + ":";
                if (!result.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                    continue;

                result = result.Substring(label.Length).Trim();
                changed = true;
                break;
            }
        }
        while (changed && result.Length > 0);

        return result;
    }

    private static string RemoveGenericAgreementOpening(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        string value = line.Trim();
        string lower = value.ToLowerInvariant();
        string[] genericOpeners =
        {
            "absolutely", "exactly", "definitely", "great idea", "you're right", "you are right",
            "i agree", "i completely agree", "i totally agree", "i understand", "of course",
            "that's great", "that sounds great", "sounds great", "thank you", "thanks"
        };

        bool matches = false;
        foreach (string opener in genericOpeners)
        {
            if (!lower.StartsWith(opener, StringComparison.Ordinal))
                continue;

            matches = true;
            break;
        }

        if (!matches)
            return value;

        int sentenceEnd = value.IndexOfAny(new[] { '.', '!', '?' });
        if (sentenceEnd >= 0 && sentenceEnd + 1 < value.Length)
            return value.Substring(sentenceEnd + 1).Trim();

        int separator = value.IndexOfAny(new[] { ',', ';', ':' });
        if (separator >= 0 && separator + 1 < value.Length)
            return value.Substring(separator + 1).Trim();

        return null;
    }

    private static bool HasUnclosedDialogueQuote(string text)
    {
        int straightQuotes = 0;
        foreach (char character in text)
            if (character == '"')
                straightQuotes++;

        return straightQuotes % 2 != 0
            || (text.IndexOf('“') >= 0 && text.IndexOf('”') < 0)
            || (text.IndexOf('‘') >= 0 && text.IndexOf('’') < 0);
    }

    private static string KeepCompleteThought(string line, bool hasUnclosedQuote,
        out string rejectionReason)
    {
        rejectionReason = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            rejectionReason = "empty line after cleanup";
            return null;
        }
        if (hasUnclosedQuote)
        {
            rejectionReason = "generation ended inside a quotation";
            return null;
        }

        string value = line.Trim();
        int lastTerminal = LastTerminalPunctuation(value);
        if (lastTerminal >= 0 && lastTerminal < value.Length - 1)
        {
            string trailing = value.Substring(lastTerminal + 1).Trim(' ', '"', '\'', '”', '’');
            if (trailing.Length > 0)
                value = value.Substring(0, lastTerminal + 1).Trim();
        }

        string[] words = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            rejectionReason = "empty line after trimming";
            return null;
        }

        if (words.Length > 30)
        {
            string shorterSentence = SelectLongestCompleteSentence(value);
            if (!string.IsNullOrWhiteSpace(shorterSentence))
            {
                value = shorterSentence;
                words = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        char finalCharacter = value[value.Length - 1];
        if (finalCharacter == '.' || finalCharacter == '!' || finalCharacter == '?')
            return value;

        if (words.Length > 12)
        {
            rejectionReason = "response ended without punctuation after " + words.Length + " words";
            return null;
        }
        if (IsIncompleteFinalWord(words[words.Length - 1]))
        {
            rejectionReason = "response ended on an incomplete word: " + words[words.Length - 1];
            return null;
        }

        return value.TrimEnd(',', ';', ':', '-') + ".";
    }

    private static string SelectLongestCompleteSentence(string value)
    {
        string best = null;
        int bestWordCount = 0;
        int sentenceStart = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '.' && value[i] != '!' && value[i] != '?')
                continue;

            string sentence = value.Substring(sentenceStart, i - sentenceStart + 1)
                .Trim(' ', '"', '\'', '“', '”', '‘', '’');
            int wordCount = sentence.Split(new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount > bestWordCount)
            {
                best = sentence;
                bestWordCount = wordCount;
            }

            sentenceStart = i + 1;
        }

        return best;
    }

    private static int LastTerminalPunctuation(string value)
    {
        for (int i = value.Length - 1; i >= 0; i--)
            if (value[i] == '.' || value[i] == '!' || value[i] == '?')
                return i;
        return -1;
    }

    private static bool IsIncompleteFinalWord(string word)
    {
        string value = word.Trim().Trim(',', ';', ':', '-', '"', '\'').ToLowerInvariant();
        switch (value)
        {
            case "a": case "an": case "the": case "and": case "but": case "or":
            case "to": case "for": case "from": case "with": case "of": case "at":
            case "in": case "on": case "by": case "while": case "because": case "that":
            case "which": case "who": case "what": case "how": case "your": case "my":
            case "our": case "their": case "you": case "i":
                return true;
            default:
                return false;
        }
    }

    private static string StripReplyLabel(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        int colon = line.IndexOf(':');
        if (colon < 0)
            return line.Trim();

        string prefix = line.Substring(0, colon).Trim().ToLowerInvariant();
        bool isReplyLabel = prefix == "reply" || prefix == "response" || prefix == "answer"
            || prefix == "assistant" || prefix == "spoken line"
            || prefix == "content operator" || prefix == "designer" || prefix == "manager"
            || prefix == "software developer" || prefix == "human resources specialist"
            || prefix == "office assistant"
            || prefix.Contains("character") || prefix.Contains("next reply")
            || prefix.Contains("would say") || prefix.Contains("will say");

        return isReplyLabel ? line.Substring(colon + 1).Trim() : line.Trim();
    }

    private static bool IsAssistantStyleReply(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        string value = line.Trim().ToLowerInvariant();
        return value.StartsWith("as an ai", StringComparison.Ordinal)
            || value.StartsWith("as a language model", StringComparison.Ordinal)
            || value.StartsWith("i am an ai", StringComparison.Ordinal)
            || value.StartsWith("i'm an ai", StringComparison.Ordinal)
            || value.StartsWith("this character", StringComparison.Ordinal)
            || value.StartsWith("the character", StringComparison.Ordinal)
            || value.StartsWith("the assistant", StringComparison.Ordinal)
            || value.StartsWith("this assistant", StringComparison.Ordinal)
            || value.StartsWith("the next reply", StringComparison.Ordinal)
            || value.StartsWith("my response", StringComparison.Ordinal)
            || value.StartsWith("i cannot roleplay", StringComparison.Ordinal)
            || value.StartsWith("i can't roleplay", StringComparison.Ordinal);
    }

    private static bool IsNarratedReply(string line, string speakerName, string participants)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        string value = line.Trim().ToLowerInvariant();
        List<string> names = new();
        if (!string.IsNullOrWhiteSpace(speakerName))
            names.Add(speakerName.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(participants))
            foreach (string participant in participants.Split(','))
                if (!string.IsNullOrWhiteSpace(participant))
                    names.Add(participant.Trim().ToLowerInvariant());

        foreach (string name in names)
        {
            if (value.StartsWith(name + "'s ", StringComparison.Ordinal)
                || value.StartsWith(name + "’s ", StringComparison.Ordinal)
                || value.StartsWith(name + " laughed", StringComparison.Ordinal)
                || value.StartsWith(name + " smiled", StringComparison.Ordinal)
                || value.StartsWith(name + " nodded", StringComparison.Ordinal)
                || value.Contains(name + " said ")
                || value.Contains(name + " replied "))
                return true;
        }

        return value.Contains("echoed through the office")
            || value.StartsWith("with a ", StringComparison.Ordinal)
            || (value.StartsWith("in a ", StringComparison.Ordinal) && value.Contains(" tone"));
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

    [Serializable]
    private class ConversationPlanDTO
    {
        public string targetAgent;
        public string topic;
        public string openingLine;
    }

    [Serializable]
    private class ConversationScriptDTO
    {
        public string reply1;
        public string reply2;
        public string reply3;
        public string reply4;
        public string reply5;
        public string reply6;
        public SocialEventDTO[] socialEvents;
    }

    [Serializable]
    private class SocialEventDTO
    {
        public string type;
        public string speaker;
        public string target;
        public string subject;
        public bool @private;
    }

    [Serializable]
    private class ActivityPlanBatchDTO
    {
        public ActivityPlanDTO[] activities;
    }

    [Serializable]
    private class ActivityPlanDTO
    {
        public string actionType;
        public string destinationMode;
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
        public string socialOpeningLine;
    }
}
