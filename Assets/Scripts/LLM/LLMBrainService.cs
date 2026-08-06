using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public sealed class ConversationParticipantContext
{
    public string agentId;
    public string displayName;
    public string relationships;
    public string currentState;
}

[Serializable]
public sealed class ConversationTurn
{
    public string speaker;
    public string line;
}

[Serializable]
public sealed class OfficeEpisodeAction
{
    public string agentId;
    public string actionType;
    public string timing;
    public string targetAgentId;
    public string destinationHint;
    public float durationSeconds;
    public string thought;
    public string reason;
}

[Serializable]
public sealed class OfficeEpisodeDialogueLine
{
    public string agentId;
    public string line;
}

[Serializable]
public sealed class OfficeEpisodeBeat
{
    public int schemaVersion;
    public string beatId;
    public string kind;
    public string topic;
    public float delaySeconds;
    public string[] participantIds;
    public string thoughtAgentId;
    public string privateThought;
    public OfficeEpisodeAction[] actions;
    public OfficeEpisodeDialogueLine[] dialogue;
    public string memory;
    public SocialMemoryEntry socialEvent;
    public string resolvesSubject;
}

[Serializable]
public sealed class OfficeEpisodePack
{
    public string packId;
    public OfficeEpisodeBeat[] beats;
    public OfficeAudienceDecision audienceDecision;
}

[Serializable]
public sealed class OfficeAudienceDecision
{
    public string decisionId;
    public string authorAgentId;
    public string authorDisplayName;
    public string question;
    public int durationSeconds = 180;
    public OfficeAudienceDecisionOption[] options;
}

[Serializable]
public sealed class OfficeAudienceDecisionOption
{
    public string optionId;
    public string label;
    public string reaction;
    public string consequence;
}

[Serializable]
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
    public string openingLine;
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
    public bool giveHeldItemToTarget;
    public bool isDirected;
    public int remainingStartAttempts;
}

public enum OfficeDestinationMode
{
    ActionPoint,
    FreePosition,
    CurrentPosition,
    FollowAgent
}

public enum WorldSchedulePhase
{
    Night,
    Morning,
    Work,
    Lunch,
    Afternoon,
    Evening
}

public class AgentProfile
{
    public string agentId;
    public string displayName;
    public string personality;
    public string conversationStyle;
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

[Serializable]
public sealed class AgentBrainEndpoint
{
    public string label;
    [Tooltip("Stable agent ids assigned to this brain. Unassigned agents use the default backend.")]
    public string[] agentIds;
    public string baseUrl;
    [Tooltip("Environment variable containing this brain's API key. Leave empty for a local endpoint.")]
    public string apiKeyEnvironmentVariable;
    public string model;
}

public class LLMBrainService : MonoBehaviour
{
    private sealed class EpisodeBackendState
    {
        public string label;
        public ILLMBackend backend;
        public int consecutiveFailures;
        public float circuitOpenUntil;
    }

    public static LLMBrainService Instance { get; private set; }
    private static readonly HashSet<string> LegacyFallbackUtterances =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "I see what you mean.",
            "That sounds reasonable to me.",
            "I had not thought about it that way.",
            "The practical details will matter.",
            "We can start small and see how it goes.",
            "That gives us something useful to work with.",
            "I think we are getting closer to an answer.",
            "It would help to keep the plan simple.",
            "That seems worth trying.",
            "I can work with that idea.",
            "Let us think through the next step.",
            "That clears up the main concern for me.",
            "That sounds like a good place to leave it for now.",
            "I think we understand each other better now.",
            "Let's pick this up again after we get some work done.",
            "I am glad we had a chance to talk about it."
        };

    [Header("Backend (OpenAI-compatible remote provider)")]
    [SerializeField] private string baseUrl = "https://api.groq.com/openai/v1";
    [Tooltip("Environment variable containing the provider API key.")]
    [SerializeField] private string apiKeyEnvironmentVariable = "GROQ_API_KEY";
    [Tooltip("Remote model used by the default provider.")]
    [SerializeField] private string model = "openai/gpt-oss-20b";
    [Tooltip("Optional provider pool. Explicit agent assignments route routine conversations; every remote entry is also available to the episode director.")]
    [SerializeField] private AgentBrainEndpoint[] agentBrains;

    [Header("Generation")]
    [SerializeField] private float temperature = 0.7f;
    [Tooltip("Seconds before an LLM request is abandoned (falls back to utility AI). Set high enough to survive the first cold model load (~15-30s) plus generation.")]
    [SerializeField] private int requestTimeoutSeconds = 60;

    [Header("Social")]
    [Tooltip("If on, nearby workers can start model-written routine conversations in addition to episode scenes.")]
    [SerializeField] private bool enableSocialReplies = true;
    [Tooltip("Maximum generation time for the complete three-reply conversation script.")]
    [SerializeField, Min(8)] private int conversationScriptTimeoutSeconds = 45;
    [Tooltip("Minimum pause between ordinary model-written chats across the whole office. Featured story and vending scenes are separate.")]
    [SerializeField, Min(8f)] private float routineConversationIntervalSeconds = 12f;

    [Header("Persistence")]
    [SerializeField, Min(15f)] private float stateSaveIntervalSeconds = 60f;
    [Tooltip("How many simulated minutes pass per real minute.")]
    [SerializeField, Range(1f, 60f)] private float simulationTimeScale = 4f;
    [Tooltip("Maximum offline catch-up applied to saved agent needs and temporary buffs. This does not advance the startup clock.")]
    [SerializeField, Range(0f, 24f)] private float maximumOfflineCatchUpHours = 8f;

    [Header("Agent Personalities")]
    [SerializeField] private AgentPersonalityEntry[] personalities;

    private ILLMBackend backend;
    private readonly Dictionary<string, ILLMBackend> backendsByAgent =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EpisodeBackendState> episodeBackends = new();
    private readonly Dictionary<string, AgentProfile> profiles = new();
    private readonly List<string> worldEvents = new();
    private readonly Dictionary<string, PersistedOfficeStoryState> officeStories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PersistedFurnitureState> furniture =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> recentGlobalTopics = new();
    private readonly List<string> recentGlobalUtterances = new();
    private readonly HashSet<string> announcedBirthdays = new();
    private bool loggedMissingApiKey;
    private float nextStateSaveTime;
    private WorldStateSnapshot persistedState;
    private readonly HashSet<string> restoredAgentIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AIWorkerAgent> runtimeAgents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PersistedAgentRuntimeState> runtimeStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentRelationshipState> relationships =
        new(StringComparer.OrdinalIgnoreCase);
    private double worldUnixSeconds;
    private float nextRoutineConversationTime;
    private int nextEpisodeBackendIndex;
    private int nextConversationBackendIndex;
    private List<OfficeEpisodeBeat> persistedEpisodeReserve = new();
    private bool startedOnNewCalendarDay;

    private LLMConversationPlanner conversationPlanner;
    private OfficeEpisodePlanner episodePlanner;

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
        DontDestroyOnLoad(gameObject);
        Application.runInBackground = true;
        persistedState = WorldStateStore.Load();
        RestoreSimulationState();
        RestoreWorldEvents();
        nextStateSaveTime = Time.unscaledTime + Mathf.Max(15f, stateSaveIntervalSeconds);

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

        ConfigureAgentBrains();
        conversationPlanner = new LLMConversationPlanner(this);
        episodePlanner = new OfficeEpisodePlanner(this);

        Debug.Log("[LLMBrainService] backend=" + (backendReady ? model : "NONE (missing API key)")
            + " | assignedBrains=" + backendsByAgent.Count
            + " | remoteEpisodeProviders=" + episodeBackends.Count
            + " | enableSocialReplies=" + enableSocialReplies
            + " | localInference=disabledForEpisodes", this);

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

        RestoreProfile(profile);

        if (!string.IsNullOrEmpty(profile.agentId))
            profiles[profile.agentId] = profile;

        if (!string.IsNullOrEmpty(profile.displayName))
            profiles[profile.displayName] = profile;

        RememberBirthdayIfToday(profile);
    }

    private void Update()
    {
        worldUnixSeconds += Time.unscaledDeltaTime * Mathf.Max(1f, simulationTimeScale);
        if (Time.unscaledTime < nextStateSaveTime)
            return;

        SaveState();
        nextStateSaveTime = Time.unscaledTime + Mathf.Max(15f, stateSaveIntervalSeconds);
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
            SaveState();
    }

    private void OnApplicationQuit()
    {
        SaveState();
    }

    private void OnDestroy()
    {
        if (Instance != this)
            return;

        SaveState();
        Instance = null;
    }

    public AgentProfile GetProfile(string agentId)
    {
        profiles.TryGetValue(agentId, out AgentProfile profile);
        return profile;
    }

    public bool EnableSocialReplies => enableSocialReplies;
    public bool HasRemoteEpisodeProvider => episodeBackends.Count > 0;
    public double WorldUnixSeconds => worldUnixSeconds;
    public DateTime WorldDateTime
    {
        get
        {
            DateTime simulated = DateTimeOffset.FromUnixTimeSeconds(
                    (long)Math.Max(0d, worldUnixSeconds))
                .ToLocalTime().DateTime;
            // The office schedule may run faster, but weekday and holidays must
            // remain aligned with the real local calendar.
            return DateTime.Today.Add(simulated.TimeOfDay);
        }
    }
    public WorldSchedulePhase SchedulePhase => ResolveSchedulePhase(WorldDateTime.Hour);
    public string ScheduleContext
    {
        get
        {
            string result = WorldDateTime.ToString("dddd HH:mm",
                CultureInfo.InvariantCulture) + ", "
                + SchedulePhase.ToString().ToLowerInvariant();
            string holiday = OfficeHolidayCalendar.Describe(WorldDateTime);
            return string.IsNullOrWhiteSpace(holiday)
                ? result : result + "; " + holiday;
        }
    }
    public double WorldSecondsFromRealSeconds(float realSeconds)
    {
        return Math.Max(0f, realSeconds) * Mathf.Max(1f, simulationTimeScale);
    }

    public float RealSecondsUntil(double targetWorldTime)
    {
        return Mathf.Max(0f, (float)((targetWorldTime - worldUnixSeconds)
            / Mathf.Max(1f, simulationTimeScale)));
    }

    public ILLMBackend GetBackendForAgent(string agentId)
    {
        if (!string.IsNullOrWhiteSpace(agentId)
            && backendsByAgent.TryGetValue(agentId.Trim(), out ILLMBackend assigned))
            return assigned;
        return backend;
    }

    public ILLMBackend GetBackendForConversation(
        List<ConversationParticipantContext> participants, string initiatorAgentId)
    {
        if (!string.IsNullOrWhiteSpace(initiatorAgentId)
            && backendsByAgent.TryGetValue(
                initiatorAgentId.Trim(), out ILLMBackend assigned)
            && assigned != null)
            return assigned;

        if (participants != null)
        {
            foreach (ConversationParticipantContext participant in participants)
            {
                if (participant == null
                    || string.IsNullOrWhiteSpace(participant.agentId)
                    || !backendsByAgent.TryGetValue(
                        participant.agentId.Trim(), out ILLMBackend participantBackend)
                    || participantBackend == null)
                    continue;
                return participantBackend;
            }
        }

        if (episodeBackends.Count > 0)
        {
            int start = Mathf.Abs(nextConversationBackendIndex)
                % episodeBackends.Count;
            for (int offset = 0; offset < episodeBackends.Count; offset++)
            {
                int index = (start + offset) % episodeBackends.Count;
                EpisodeBackendState provider = episodeBackends[index];
                if (provider?.backend == null || provider.backend.IsLocal
                    || Time.unscaledTime < provider.circuitOpenUntil)
                    continue;
                nextConversationBackendIndex =
                    (index + 1) % episodeBackends.Count;
                return provider.backend;
            }
        }

        return GetBackendForAgent(initiatorAgentId);
    }

    private bool HasAnyBackend => backend != null || backendsByAgent.Count > 0;

    public bool TryReserveRoutineConversation()
    {
        if (!enableSocialReplies || !HasAnyBackend
            || Time.unscaledTime < nextRoutineConversationTime)
            return false;

        nextRoutineConversationTime = Time.unscaledTime
            + Mathf.Max(8f, routineConversationIntervalSeconds);
        return true;
    }

    public bool CanStartRoutineConversation
    {
        get
        {
            if (!enableSocialReplies || !HasAnyBackend
                || Time.unscaledTime < nextRoutineConversationTime)
                return false;
            return true;
        }
    }

    public void RegisterRuntimeAgent(AIWorkerAgent agent)
    {
        if (agent == null || string.IsNullOrWhiteSpace(agent.AgentId))
            return;

        runtimeAgents[agent.AgentId] = agent;
        if (runtimeStates.TryGetValue(agent.AgentId, out PersistedAgentRuntimeState saved))
            agent.RestoreRuntimeState(saved, CalculateOfflineWorldSeconds(saved));
    }

    public void UnregisterRuntimeAgent(AIWorkerAgent agent)
    {
        if (agent == null || string.IsNullOrWhiteSpace(agent.AgentId))
            return;
        runtimeStates[agent.AgentId] = agent.CaptureRuntimeState();
        runtimeAgents.Remove(agent.AgentId);
    }

    public float GetRelationshipScore(string firstAgentId, string secondAgentId)
    {
        AgentRelationshipState relationship = GetRelationship(firstAgentId, secondAgentId, false);
        return relationship == null
            ? 0f
            : relationship.affinity + relationship.trust - relationship.tension;
    }

    public string BuildRelationshipContext(string firstAgentId, string secondAgentId)
    {
        AgentRelationshipState relationship = GetRelationship(firstAgentId, secondAgentId, false);
        if (relationship == null || relationship.interactions == 0)
            return "They do not know each other well yet.";

        string familiarity = relationship.interactions >= 8 ? "very familiar"
            : relationship.interactions >= 3 ? "familiar" : "recent acquaintances";
        string tone = relationship.tension > relationship.trust + 2f ? "with unresolved tension"
            : relationship.trust >= 5f ? "and they trust each other"
            : relationship.affinity >= 4f ? "and generally enjoy each other's company"
            : "and are still learning how to work together";
        return familiarity + " " + tone + ". Last shared event: "
            + (string.IsNullOrWhiteSpace(relationship.lastEvent)
                ? "ordinary office conversation" : relationship.lastEvent);
    }

    public void RecordRelationshipInteraction(string firstAgentId, string secondAgentId,
        string eventType, string subject)
    {
        AgentRelationshipState relationship = GetRelationship(firstAgentId, secondAgentId, true);
        if (relationship == null)
            return;

        relationship.interactions++;
        relationship.affinity = Mathf.Clamp(relationship.affinity + 0.35f, -10f, 10f);
        switch ((eventType ?? "").Trim().ToLowerInvariant())
        {
            case "secret":
                relationship.trust += 0.8f;
                break;
            case "gossip":
                relationship.affinity += 0.4f;
                relationship.tension += 0.25f;
                break;
            case "promise":
            case "plan":
            case "invitation":
                relationship.trust += 0.35f;
                break;
            case "favor_done":
                relationship.trust += 1.5f;
                relationship.affinity += 0.75f;
                relationship.tension -= 0.5f;
                break;
            case "conflict":
                relationship.tension += 1.5f;
                relationship.affinity -= 0.5f;
                break;
        }

        relationship.affinity = Mathf.Clamp(relationship.affinity, -10f, 10f);
        relationship.trust = Mathf.Clamp(relationship.trust, 0f, 10f);
        relationship.tension = Mathf.Clamp(relationship.tension, 0f, 10f);
        relationship.lastEvent = IsStaleSnackMystery(subject)
            ? "ordinary office conversation"
            : string.IsNullOrWhiteSpace(subject)
            ? eventType : subject.Trim();
        relationship.lastInteractionWorldTime = worldUnixSeconds;
    }

    public void Remember(string agentId, string line)
    {
        AgentProfile profile = GetProfile(agentId);
        if (profile != null && !string.IsNullOrEmpty(line)
            && !IsStaleSnackMystery(line))
            AppendMemory(profile, line);
    }

    public void RememberWorldEvent(string description)
    {
        if (string.IsNullOrWhiteSpace(description)
            || IsStaleSnackMystery(description))
            return;
        worldEvents.Add(description.Trim());
        while (worldEvents.Count > 12)
            worldEvents.RemoveAt(0);
    }

    public void RecordOverheardGossip(string agentId, string sourceNames,
        string subject, string reliability)
    {
        AgentProfile profile = GetProfile(agentId);
        if (profile == null || string.IsNullOrWhiteSpace(subject)
            || IsStaleSnackMystery(subject))
            return;
        profile.socialMemory.Add(new SocialMemoryEntry
        {
            type = "gossip",
            sourceAgent = string.IsNullOrWhiteSpace(sourceNames)
                ? "some coworkers" : sourceNames.Trim(),
            targetAgent = "",
            subject = subject.Trim(),
            isPrivate = true,
            status = string.IsNullOrWhiteSpace(reliability)
                ? "accurate" : reliability.Trim()
        });
        while (profile.socialMemory.Count > 20)
            profile.socialMemory.RemoveAt(0);
        Remember(agentId, subject);
    }

    public void RecordPersonalReveal(string agentId, string subject)
    {
        AgentProfile profile = GetProfile(agentId);
        if (profile == null || string.IsNullOrWhiteSpace(subject)
            || IsStaleSnackMystery(subject))
            return;
        foreach (SocialMemoryEntry entry in profile.socialMemory)
            if (entry != null && entry.status == "scheduled"
                && TextUtils.TextSimilarity(entry.subject, subject) >= 0.7f)
                return;
        profile.socialMemory.Add(new SocialMemoryEntry
        {
            type = "plan",
            sourceAgent = agentId,
            targetAgent = "",
            subject = subject.Trim(),
            isPrivate = true,
            status = "scheduled"
        });
        while (profile.socialMemory.Count > 20)
            profile.socialMemory.RemoveAt(0);
        RememberWorldEvent(subject);
    }

    public void RecordOfficeStoryStarted(string storyId)
    {
        RecordOfficeStoryStarted(storyId, "", null);
    }

    public void RecordOfficeStoryStarted(string storyId, string actorAgentId,
        IEnumerable<string> participantAgentIds)
    {
        PersistedOfficeStoryState state = GetOrCreateOfficeStory(storyId);
        if (state == null)
            return;
        state.started++;
        state.visualStage = 1;
        state.stageChangedWorldTime = worldUnixSeconds;
        state.actorAgentId = actorAgentId ?? "";
        state.participantAgentIds = participantAgentIds != null
            ? new List<string>(participantAgentIds)
            : new List<string>();
        state.visitorAgentIds = new List<string>();
        state.startedCalendarDate = WorldDateTime.ToString(
            "yyyy-MM-dd", CultureInfo.InvariantCulture);
        SaveState();
    }

    public void RecordOfficeStoryResolved(string storyId)
    {
        PersistedOfficeStoryState state = GetOrCreateOfficeStory(storyId);
        if (state == null)
            return;
        if (state.started == 0)
            state.started = 1;
        state.resolved = Mathf.Min(state.started, state.resolved + 1);
        state.visualStage = 3;
        state.stageChangedWorldTime = worldUnixSeconds;
        SaveState();
    }

    public void SetOfficeStoryVisualStage(string storyId, int stage)
    {
        PersistedOfficeStoryState state = GetOrCreateOfficeStory(storyId);
        if (state == null)
            return;
        state.visualStage = Mathf.Clamp(stage, 0, 4);
        state.stageChangedWorldTime = worldUnixSeconds;
        SaveState();
    }

    public PersistedOfficeStoryState GetOfficeStoryState(string storyId)
    {
        officeStories.TryGetValue(
            storyId ?? "", out PersistedOfficeStoryState state);
        return state;
    }

    public void RecordOfficeStoryVisit(string storyId, string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return;
        PersistedOfficeStoryState state = GetOrCreateOfficeStory(storyId);
        if (state == null)
            return;
        state.visitorAgentIds ??= new List<string>();
        if (state.visitorAgentIds.Exists(existing => string.Equals(
                existing, agentId, StringComparison.OrdinalIgnoreCase)))
            return;
        state.visitorAgentIds.Add(agentId);
        SaveState();
    }

    public bool HasPendingOfficeStory(string storyId)
    {
        return officeStories.TryGetValue(
                storyId ?? "", out PersistedOfficeStoryState state)
            && state.started > state.resolved;
    }

    public bool HasOfficeStoryState(string storyId)
    {
        return !string.IsNullOrWhiteSpace(storyId)
            && officeStories.ContainsKey(storyId);
    }

    private PersistedOfficeStoryState GetOrCreateOfficeStory(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId))
            return null;
        if (!officeStories.TryGetValue(
                storyId, out PersistedOfficeStoryState state))
        {
            state = new PersistedOfficeStoryState { storyId = storyId };
            officeStories.Add(storyId, state);
        }
        return state;
    }

    public void RememberFurniture(PersistedFurnitureState state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.socketId)
            || string.IsNullOrWhiteSpace(state.eventId))
            return;
        furniture[state.socketId] = state;
        SaveState();
    }

    public List<PersistedFurnitureState> RestoreFurniture()
    {
        return new List<PersistedFurnitureState>(furniture.Values);
    }

    public async Task<OfficeEpisodePack> GenerateEpisodePackAsync(
        List<AIWorkerAgent> workers, int requestedCount,
        bool requestAudienceDecision = false)
    {
        if (episodePlanner == null || workers == null || workers.Count < 2
            || episodeBackends.Count == 0)
            return null;

        int providerCount = episodeBackends.Count;
        int startIndex = Mathf.Abs(nextEpisodeBackendIndex) % providerCount;
        for (int offset = 0; offset < providerCount; offset++)
        {
            int index = (startIndex + offset) % providerCount;
            EpisodeBackendState provider = episodeBackends[index];
            if (provider?.backend == null
                || Time.unscaledTime < provider.circuitOpenUntil)
                continue;

            OfficeEpisodePack pack = await episodePlanner.GenerateEpisodePackAsync(
                provider.backend, provider.label, workers,
                Mathf.Clamp(requestedCount, 4, 8),
                temperature, Mathf.Min(requestTimeoutSeconds, 45),
                requestAudienceDecision);
            if (pack != null && pack.beats != null && pack.beats.Length > 0)
            {
                provider.consecutiveFailures = 0;
                provider.circuitOpenUntil = 0f;
                nextEpisodeBackendIndex = (index + 1) % providerCount;
                Debug.Log("[Episode director] buffered " + pack.beats.Length
                    + " beats from " + provider.label + ".", this);
                return pack;
            }

            provider.consecutiveFailures++;
            if (provider.consecutiveFailures >= 3)
            {
                provider.circuitOpenUntil = Time.unscaledTime + 60f;
                provider.consecutiveFailures = 0;
                Debug.LogWarning("[Episode director] " + provider.label
                    + " circuit opened for 60 seconds after repeated failures.", this);
            }
        }

        return null;
    }

    public async Task<OfficeNewspaper> GenerateDailyNewspaperAsync(
        string calendarDate, string interactionContext)
    {
        if (episodeBackends.Count == 0)
            return null;
        StringBuilder source = new();
        source.Append("Date: ").AppendLine(calendarDate);
        int worldStart = Mathf.Max(0, worldEvents.Count - 10);
        for (int i = worldStart; i < worldEvents.Count; i++)
            source.Append("Event: ").AppendLine(
                TextUtils.CleanShortText(worldEvents[i], 24));
        int lineStart = Mathf.Max(0, recentGlobalUtterances.Count - 6);
        for (int i = lineStart; i < recentGlobalUtterances.Count; i++)
            source.Append("Quote candidate: ").AppendLine(
                TextUtils.CleanShortText(recentGlobalUtterances[i], 18));
        if (!string.IsNullOrWhiteSpace(interactionContext))
            source.Append("Visitor activity: ").AppendLine(
                TextUtils.CleanShortText(interactionContext, 80));

        List<ChatMessage> messages = new()
        {
            new ChatMessage("user",
                "Create a concise English daily newspaper for a persistent fictional office. "
                + "Use only supplied facts. Prefer consequences and relationship changes over "
                + "generic work summaries. Do not invent purchases, visitors, or completed events. "
                + "The headline is under 9 words; summary under 30 words; exactly 3 stories under "
                + "30 words each; quote under 18 words; every remaining field under 24 words. "
                + "Return JSON only: {\"date\":string,\"headline\":string,\"summary\":string,"
                + "\"stories\":[string,string,string],\"quote\":string,"
                + "\"decisionResult\":string,\"visitorAcknowledgement\":string,"
                + "\"tomorrowTeaser\":string}.\n\n" + source)
        };
        LLMOptions options = new()
        {
            requestLabel = "DailyNewspaper",
            temperature = 0.68f,
            maxTokens = 520,
            jsonMode = true,
            reasoningEffort = "low",
            excludeReasoning = true,
            timeoutSeconds = Mathf.Min(requestTimeoutSeconds, 35),
            maxRetries = 0,
            highPriority = false
        };
        for (int i = 0; i < episodeBackends.Count; i++)
        {
            EpisodeBackendState provider = episodeBackends[
                (nextEpisodeBackendIndex + i) % episodeBackends.Count];
            if (provider?.backend == null
                || Time.unscaledTime < provider.circuitOpenUntil)
                continue;
            try
            {
                string raw = await provider.backend.CompleteAsync(messages, options);
                OfficeNewspaper newspaper = JsonUtility.FromJson<OfficeNewspaper>(
                    TextUtils.ExtractJson(raw));
                if (IsValidNewspaper(newspaper))
                {
                    newspaper.date = calendarDate;
                    return newspaper;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Daily newspaper] " + provider.label
                    + " failed: " + exception.Message, this);
            }
        }
        return null;
    }

    private static bool IsValidNewspaper(OfficeNewspaper newspaper)
    {
        if (newspaper == null || string.IsNullOrWhiteSpace(newspaper.headline)
            || string.IsNullOrWhiteSpace(newspaper.summary)
            || newspaper.stories == null || newspaper.stories.Length < 2)
            return false;
        newspaper.headline = TextUtils.CleanShortText(newspaper.headline, 10);
        newspaper.summary = TextUtils.CleanShortText(newspaper.summary, 32);
        newspaper.quote = TextUtils.CleanShortText(newspaper.quote, 20);
        newspaper.decisionResult = TextUtils.CleanShortText(
            newspaper.decisionResult, 28);
        newspaper.visitorAcknowledgement = TextUtils.CleanShortText(
            newspaper.visitorAcknowledgement, 28);
        newspaper.tomorrowTeaser = TextUtils.CleanShortText(
            newspaper.tomorrowTeaser, 28);
        List<string> stories = new();
        foreach (string story in newspaper.stories)
            if (!string.IsNullOrWhiteSpace(story) && stories.Count < 3)
                stories.Add(TextUtils.CleanShortText(story, 32));
        newspaper.stories = stories.ToArray();
        return newspaper.stories.Length >= 2;
    }

    public async Task<ConversationScript> GenerateConversationAsync(
        List<ConversationParticipantContext> participants,
        string openingSpeaker,
        string topic,
        List<string> speakerOrder)
    {
        return await conversationPlanner.GenerateConversationAsync(
            participants, openingSpeaker, topic, speakerOrder,
            temperature, requestTimeoutSeconds,
            conversationScriptTimeoutSeconds);
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

    public void CompleteScheduledEpisodeMemory(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return;

        HashSet<AgentProfile> uniqueProfiles = new(profiles.Values);
        foreach (AgentProfile profile in uniqueProfiles)
            foreach (SocialMemoryEntry entry in profile.socialMemory)
                if (entry != null && entry.status == "scheduled"
                    && TextUtils.TextSimilarity(entry.subject, subject) >= 0.7f)
                    entry.status = "completed";
    }

    public List<OfficeEpisodeBeat> RestoreEpisodeReserve()
    {
        return persistedEpisodeReserve != null
            ? new List<OfficeEpisodeBeat>(persistedEpisodeReserve)
            : new List<OfficeEpisodeBeat>();
    }

    public void SetEpisodeReserve(IEnumerable<OfficeEpisodeBeat> beats)
    {
        persistedEpisodeReserve = new List<OfficeEpisodeBeat>();
        if (beats == null)
            return;
        foreach (OfficeEpisodeBeat beat in beats)
            if (beat != null && HasEpisodeThought(beat)
                && !IsStaleSnackMysteryBeat(beat))
                persistedEpisodeReserve.Add(beat);
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
        for (int i = 0; i < profile.memory.Count; i++)
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
        if (!RequiresApiKey())
            return "";
        return ResolveEnvironmentVariable(apiKeyEnvironmentVariable);
    }

    private void ConfigureAgentBrains()
    {
        backendsByAgent.Clear();
        episodeBackends.Clear();
        if (backend != null && !backend.IsLocal)
            episodeBackends.Add(new EpisodeBackendState
            {
                label = "default " + model,
                backend = backend
            });
        if (agentBrains == null)
            return;

        foreach (AgentBrainEndpoint slot in agentBrains)
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.baseUrl)
                || string.IsNullOrWhiteSpace(slot.model))
                continue;

            string key = ResolveEnvironmentVariable(slot.apiKeyEnvironmentVariable);
            if (RequiresApiKey(slot.baseUrl) && string.IsNullOrWhiteSpace(key))
            {
                Debug.LogWarning("[LLMBrainService] brain " +
                    (string.IsNullOrWhiteSpace(slot.label) ? slot.model : slot.label) +
                    " disabled because its API key environment variable is empty.", this);
                continue;
            }

            ILLMBackend slotBackend = new OpenAICompatibleBackend(
                slot.baseUrl, key, slot.model);
            string providerLabel = string.IsNullOrWhiteSpace(slot.label)
                ? slot.model : slot.label.Trim();
            if (!slotBackend.IsLocal)
                episodeBackends.Add(new EpisodeBackendState
                {
                    label = providerLabel,
                    backend = slotBackend
                });

            if (slot.agentIds == null)
                continue;
            foreach (string rawAgentId in slot.agentIds)
            {
                if (string.IsNullOrWhiteSpace(rawAgentId))
                    continue;
                string agentId = rawAgentId.Trim();
                backendsByAgent[agentId] = slotBackend;
                Debug.Log("[LLMBrainService] " + agentId + " brain=" +
                    providerLabel + " (" + slot.model + ")", this);
            }
        }
    }

    private static string ResolveEnvironmentVariable(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            return "";
        string name = variableName.Trim();
        // User-scoped values are managed by Configure-LLMKeys.ps1. Prefer them
        // over a stale value inherited when Unity was launched.
        string value = Environment.GetEnvironmentVariable(
            name, EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(value))
            value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            value = Environment.GetEnvironmentVariable(
                name, EnvironmentVariableTarget.Machine);
        return value?.Trim() ?? "";
    }

    private void RestoreWorldEvents()
    {
        if (persistedState?.worldEvents == null)
            return;

        foreach (string worldEvent in persistedState.worldEvents)
        {
            if (persistedState.version < 7
                && IsLegacyPackageKnowledge(worldEvent))
                continue;
            if (persistedState.version < 8
                && IsLegacyRoutineNarrativeFact(worldEvent))
                continue;
            RememberWorldEvent(worldEvent);
        }
    }

    private void RestoreSimulationState()
    {
        double realNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double restoredWorldTime = persistedState != null
            && persistedState.worldUnixSeconds > 0d
                ? persistedState.worldUnixSeconds : realNow;
        double worldTimeShift = realNow - restoredWorldTime;
        // A fresh process always starts from the computer clock. Rebase saved
        // world timestamps below so accelerated play from a previous run cannot
        // make a morning restart believe it is still evening.
        worldUnixSeconds = realNow;
        Replace(recentGlobalTopics,
            persistedState?.recentNarrativeTopics, 24);
        Replace(recentGlobalUtterances,
            persistedState?.recentNarrativeUtterances, 48);

        if (persistedState != null && DateTime.TryParse(persistedState.savedAtUtc,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out DateTime savedAtUtc))
        {
            startedOnNewCalendarDay = savedAtUtc.ToLocalTime().Date
                != DateTime.Now.Date;
        }

        if (startedOnNewCalendarDay)
        {
            recentGlobalTopics.Clear();
            recentGlobalUtterances.Clear();
        }

        if (persistedState?.agentRuntime != null)
            foreach (PersistedAgentRuntimeState state in persistedState.agentRuntime)
                if (state != null && !string.IsNullOrWhiteSpace(state.agentId))
                {
                    if (startedOnNewCalendarDay)
                    {
                        state.lastAction = "";
                        state.activeGoal = "";
                    }
                    runtimeStates[state.agentId] = state;
                }

        if (persistedState?.relationships != null)
            foreach (AgentRelationshipState relationship in persistedState.relationships)
                if (relationship != null)
                {
                    if (IsStaleSnackMystery(relationship.lastEvent))
                        relationship.lastEvent = "ordinary office conversation";
                    if (relationship.lastInteractionWorldTime > 0d)
                        relationship.lastInteractionWorldTime += worldTimeShift;
                    relationships[RelationshipKey(
                        relationship.firstAgentId, relationship.secondAgentId)] = relationship;
                }

        officeStories.Clear();
        if (persistedState?.officeStories != null)
            foreach (PersistedOfficeStoryState story in persistedState.officeStories)
                if (story != null && !string.IsNullOrWhiteSpace(story.storyId))
                {
                    story.participantAgentIds ??= new List<string>();
                    story.visitorAgentIds ??= new List<string>();
                    if (story.stageChangedWorldTime > 0d)
                    {
                        bool resolvedAftermath = story.started > 0
                            && story.resolved >= story.started;
                        story.stageChangedWorldTime = startedOnNewCalendarDay
                            && resolvedAftermath
                                ? realNow - 86400d
                                : story.stageChangedWorldTime + worldTimeShift;
                    }
                    if (string.IsNullOrWhiteSpace(story.startedCalendarDate)
                        && story.started > 0)
                        story.startedCalendarDate = WorldDateTime.ToString(
                            "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (story.started > story.resolved && story.visualStage == 0)
                        story.visualStage = 1;
                    else if (story.started > 0 && story.resolved >= story.started
                        && story.visualStage == 0)
                        story.visualStage = 4;
                    officeStories[story.storyId] = story;
                }

        furniture.Clear();
        if (persistedState?.furniture != null)
            foreach (PersistedFurnitureState item in persistedState.furniture)
                if (item != null && !string.IsNullOrWhiteSpace(item.socketId)
                    && !string.IsNullOrWhiteSpace(item.eventId))
                    furniture[item.socketId] = item;

        persistedEpisodeReserve = persistedState?.episodeReserve != null
            ? new List<OfficeEpisodeBeat>(persistedState.episodeReserve)
            : new List<OfficeEpisodeBeat>();
        if (startedOnNewCalendarDay
            || (persistedState != null && persistedState.version < 8))
            persistedEpisodeReserve.Clear();
        persistedEpisodeReserve.RemoveAll(beat =>
            !HasEpisodeThought(beat) || IsStaleSnackMysteryBeat(beat));
    }

    private void RestoreProfile(AgentProfile profile)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.agentId)
            || !restoredAgentIds.Add(profile.agentId)
            || persistedState?.agents == null)
            return;

        PersistedAgentState saved = persistedState.agents.Find(candidate =>
            candidate != null && string.Equals(candidate.agentId, profile.agentId,
                StringComparison.OrdinalIgnoreCase));
        if (saved == null)
            return;

        Replace(profile.memory, saved.memory, 30);
        ReplaceSocialMemory(profile.socialMemory, saved.socialMemory, 30);
        if (persistedState.version < 3)
            foreach (SocialMemoryEntry entry in profile.socialMemory)
                if (entry != null && string.Equals(
                        entry.status, "open", StringComparison.OrdinalIgnoreCase))
                    entry.status = "dormant";
        Replace(profile.recentTopics, saved.recentTopics, 8);
        Replace(profile.recentOpenings, saved.recentOpenings, 8);
        Replace(profile.recentUtterances, saved.recentUtterances, 10);
        if (startedOnNewCalendarDay)
        {
            profile.recentTopics.Clear();
            profile.recentOpenings.Clear();
            profile.recentUtterances.Clear();
        }
        profile.memory.RemoveAll(IsStaleSnackMystery);
        if (persistedState.version < 7)
            profile.memory.RemoveAll(IsLegacyPackageKnowledge);
        if (persistedState.version < 8)
            profile.memory.RemoveAll(IsLegacyRoutineNarrativeFact);
        profile.socialMemory.RemoveAll(entry => entry == null
            || string.IsNullOrWhiteSpace(entry.type)
            || string.IsNullOrWhiteSpace(entry.sourceAgent)
            || string.IsNullOrWhiteSpace(entry.subject)
            || IsStaleSnackMystery(entry.subject)
            || (persistedState.version < 7
                && IsLegacyPackageKnowledge(entry.subject))
            || (persistedState.version < 8
                && IsLegacyRoutineNarrativeFact(entry.subject)));
        profile.recentTopics.RemoveAll(IsStaleSnackMystery);
        profile.recentOpenings.RemoveAll(IsStaleSnackMystery);
        profile.recentUtterances.RemoveAll(IsStaleSnackMystery);
        profile.recentUtterances.RemoveAll(IsLegacyFallbackUtterance);
        if (persistedState.version < 7)
        {
            profile.recentTopics.RemoveAll(IsLegacyPackageKnowledge);
            profile.recentOpenings.RemoveAll(IsLegacyPackageKnowledge);
            profile.recentUtterances.RemoveAll(IsLegacyPackageKnowledge);
        }
    }

    private static void Replace(List<string> target, List<string> source, int capacity)
    {
        target.Clear();
        if (source == null)
            return;

        int start = Mathf.Max(0, source.Count - Mathf.Max(1, capacity));
        for (int i = start; i < source.Count; i++)
        {
            string value = source[i]?.Trim();
            if (!string.IsNullOrWhiteSpace(value)
                && !target.Exists(existing => string.Equals(existing, value,
                    StringComparison.OrdinalIgnoreCase)))
                target.Add(value);
        }
    }

    private static bool IsLegacyFallbackUtterance(string line)
    {
        return !string.IsNullOrWhiteSpace(line)
            && LegacyFallbackUtterances.Contains(line.Trim());
    }

    private static bool IsLegacyPackageKnowledge(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string lower = text.ToLowerInvariant();
        return lower.Contains("unclaimed package")
            || lower.Contains("no recipient name")
            || lower.Contains("unexpected_package")
            || (lower.Contains("package")
                && lower.Contains("office address"));
    }

    private static bool IsLegacyRoutineNarrativeFact(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || TextUtils.ContainsIgnoreCase(
                text, "physical vending purchase"))
            return false;
        string lower = text.ToLowerInvariant();
        bool genericFood = lower.Contains("snack")
            || lower.Contains("coffee") || lower.Contains("espresso");
        bool finishedWork = lower.Contains("finished work")
            || lower.Contains("finished the task")
            || lower.Contains("finished the project")
            || lower.Contains("completed the task")
            || lower.Contains("completed our work")
            || lower.Contains("task is done")
            || lower.Contains("ahead of schedule")
            || lower.Contains("beat the deadline")
            || lower.Contains("early finish");
        return genericFood || finishedWork;
    }

    internal static bool IsStaleSnackMystery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string lower = text.ToLowerInvariant();
        if (lower.Contains("sketchpad"))
            return lower.Trim(' ', '.', '!', '?') == "sketchpad"
                || lower.Contains("misplaced sketchpad")
                || lower.Contains("sketchpad found")
                || lower.Contains("sketchpad on your desk")
                || lower.Contains("sketchpad left")
                || lower.Contains("spotted a sketchpad")
                || lower.Contains("where the sketchpad");
        if (!lower.Contains("snack"))
            return false;
        return lower.Contains("missing snack")
            || lower.Contains("lost snack")
            || lower.Contains("snack is missing")
            || lower.Contains("snacks are missing")
            || lower.Contains("emergency snack")
            || lower.Contains("stolen snack")
            || lower.Contains("snack disappeared")
            || lower.Contains("snacks disappeared");
    }

    private static bool IsStaleSnackMysteryBeat(OfficeEpisodeBeat beat)
    {
        if (beat == null)
            return false;
        if (IsStaleSnackMystery(beat.topic)
            || IsStaleSnackMystery(beat.memory)
            || IsStaleSnackMystery(beat.resolvesSubject)
            || IsStaleSnackMystery(beat.socialEvent?.subject))
            return true;
        if (beat.dialogue != null)
            foreach (OfficeEpisodeDialogueLine line in beat.dialogue)
                if (IsStaleSnackMystery(line?.line))
                    return true;
        if (beat.actions != null)
            foreach (OfficeEpisodeAction action in beat.actions)
                if (IsStaleSnackMystery(action?.reason)
                    || IsStaleSnackMystery(action?.thought))
                    return true;
        return false;
    }

    private static bool HasEpisodeThought(OfficeEpisodeBeat beat)
    {
        return beat != null
            && beat.schemaVersion >= 4
            && !string.IsNullOrWhiteSpace(beat.thoughtAgentId)
            && !string.IsNullOrWhiteSpace(beat.privateThought);
    }

    private static void ReplaceSocialMemory(List<SocialMemoryEntry> target,
        List<SocialMemoryEntry> source, int capacity)
    {
        target.Clear();
        if (source == null)
            return;

        int start = Mathf.Max(0, source.Count - Mathf.Max(1, capacity));
        for (int i = start; i < source.Count; i++)
            if (source[i] != null)
                target.Add(source[i]);
    }

    private void SaveState()
    {
        foreach (KeyValuePair<string, AIWorkerAgent> entry in runtimeAgents)
            if (entry.Value != null)
                runtimeStates[entry.Key] = entry.Value.CaptureRuntimeState();

        HashSet<AgentProfile> uniqueProfiles = new(profiles.Values);
        WorldStateStore.Save(uniqueProfiles, worldEvents, worldUnixSeconds,
            runtimeStates.Values, relationships.Values, persistedEpisodeReserve,
            officeStories.Values, furniture.Values, recentGlobalTopics,
            recentGlobalUtterances);
    }

    private double CalculateOfflineWorldSeconds(PersistedAgentRuntimeState state)
    {
        if (state == null || state.capturedAtUnixMilliseconds <= 0)
            return 0d;
        double realSeconds = Math.Max(0d,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            - state.capturedAtUnixMilliseconds) / 1000d;
        return Math.Min(realSeconds * Mathf.Max(1f, simulationTimeScale),
            Mathf.Max(0f, maximumOfflineCatchUpHours) * 3600d);
    }

    private AgentRelationshipState GetRelationship(string firstAgentId,
        string secondAgentId, bool create)
    {
        if (string.IsNullOrWhiteSpace(firstAgentId)
            || string.IsNullOrWhiteSpace(secondAgentId)
            || string.Equals(firstAgentId, secondAgentId, StringComparison.OrdinalIgnoreCase))
            return null;

        string key = RelationshipKey(firstAgentId, secondAgentId);
        if (relationships.TryGetValue(key, out AgentRelationshipState relationship)
            || !create)
            return relationship;

        bool firstBeforeSecond = string.Compare(firstAgentId, secondAgentId,
            StringComparison.OrdinalIgnoreCase) <= 0;
        relationship = new AgentRelationshipState
        {
            firstAgentId = firstBeforeSecond ? firstAgentId : secondAgentId,
            secondAgentId = firstBeforeSecond ? secondAgentId : firstAgentId
        };
        relationships[key] = relationship;
        return relationship;
    }

    private static string RelationshipKey(string firstAgentId, string secondAgentId)
    {
        string first = (firstAgentId ?? "").Trim();
        string second = (secondAgentId ?? "").Trim();
        return string.Compare(first, second, StringComparison.OrdinalIgnoreCase) <= 0
            ? first.ToLowerInvariant() + "|" + second.ToLowerInvariant()
            : second.ToLowerInvariant() + "|" + first.ToLowerInvariant();
    }

    private static WorldSchedulePhase ResolveSchedulePhase(int hour)
    {
        if (hour < 7 || hour >= 22)
            return WorldSchedulePhase.Night;
        if (hour < 9)
            return WorldSchedulePhase.Morning;
        if (hour < 12)
            return WorldSchedulePhase.Work;
        if (hour < 14)
            return WorldSchedulePhase.Lunch;
        if (hour < 18)
            return WorldSchedulePhase.Afternoon;
        return WorldSchedulePhase.Evening;
    }

    private bool RequiresApiKey()
    {
        return RequiresApiKey(baseUrl);
    }

    private static bool RequiresApiKey(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        Uri uri;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri))
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
            " API key is empty. Configure Api Key Environment Variable and launch Unity from a process that can read it.",
            this);
    }

    [ContextMenu("Test Connection")]
    private async void TestConnection()
    {
        string resolvedApiKey = ResolveApiKey();
        if (RequiresApiKey() && string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            LogMissingApiKey();
            Debug.Log(nameof(LLMBrainService) +
                " test response: (not sent - missing API key)", this);
            return;
        }

        backend = new OpenAICompatibleBackend(baseUrl, resolvedApiKey, model);
        conversationPlanner = new LLMConversationPlanner(this);
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
            (result ?? "(null/failed - check the local service or remote key, quota, URL, and model)"));
    }
}
