using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public sealed class ConversationIntent
{
    public string initiatorAgentId;
    public string initiatorName;
    public string intendedPartnerName;
    public string[] requiredParticipantNames;
    public string topic;
    public string openingLine;
    public bool generatedByModel;
    public OfficeActionType actionType;
    public float createdAt;
    public float expiresAt;
    public System.Action onOpeningSpoken;
    public ConversationScript preparedScript;
    public bool routineConversationReserved;

    public bool IsValid(float now)
    {
        return now <= expiresAt;
    }
}

public sealed class AgentActivity
{
    public OfficeActionType actionType;
    public OfficeDestinationMode destinationMode;
    public string destinationHint;
    public string sequenceId;
    public int sequenceStep;
    public string objective;
    public Vector2 destination;
    public OfficeActionPoint actionPoint;
    public AIWorkerAgent targetAgent;
    public float duration;
    public string reason;
    public string thought;
    public string customActionLabel;
    public float energyChange;
    public float focusChange;
    public float socialChange;
    public float productivityChange;
    public string socialMemorySubject;
    public bool completesSocialCommitment;
    public bool giveHeldItemToTarget;
    public bool interactionAttempted;
    public bool waitingForConversationLogged;
    public bool interactionSucceeded;
    public bool highPriorityConversation;
    public bool isPreparedBirthdayReaction;
    public bool waitingForGeneratedSpeech;
    public float generatedSpeechDeadline;
    public ConversationScript preparedScript;
    public List<string> preparedSpeechLines;
    public bool routineConversationReserved;
}

public enum AgentEmotion
{
    Neutral,
    Happy,
    Lol,
    Romantic,
    Angry,
    Sad,
    Shocked,
    Crying,
    Surprised,
    Cool,
    Confused,
    Sleepy,
    FacePalm
}

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(OfficeWorkerMotor2D))]
[RequireComponent(typeof(AgentPresentation2D))]
[RequireComponent(typeof(AgentConversationController))]
[RequireComponent(typeof(AgentNavigationController))]

[RequireComponent(typeof(AgentEffects))]
public class AIWorkerAgent : MonoBehaviour
{
    private sealed class ThoughtClaim
    {
        public string agentId;
        public float shownAt;
    }

    private static readonly Dictionary<string, ThoughtClaim> RecentThoughtClaims = new();
    private const float DuplicateThoughtWindowSeconds = 4f;
    private static AIWorkerAgent activePhoneCaller;

    private sealed class PreparedBirthdaySurprise
    {
        public AIWorkerAgent creator;
        public Vector2 location;
        public string description;
        public string thankYouLine;
    }

    private enum WorkerState
    {
        Thinking,
        Moving,
        Acting
    }

    [Header("Identity")]
    [Tooltip("Stable identity, biography, traits, interests, and speech style for this worker.")]
    [SerializeField] private AgentProfileSO profile;
    private string inferredAgentType = "";

    [Header("References")]
    public OfficeGrid2D grid;

    [Header("Assigned Workstation")]
    [Tooltip("Drag the specific desk this agent owns into this slot")]
    public OfficeActionPoint assignedDesk;

    [Header("Movement")]
    public float speed = 2f;
    public float arriveDistance = 0.08f;

    [Header("Needs")]
    [Range(0f, 100f)] public float energy = 70f;
    [Range(0f, 100f)] public float focus = 70f;
    [Range(0f, 100f)] public float social = 70f;
    public float productivity = 0f;

    [Header("Need Decay Per Second")]
    public float energyDecay = 0.7f;
    public float focusDecay = 0.5f;
    public float socialDecay = 0.35f;

    [Header("Decision")]
    public float decisionDelay = 1f;
    public float randomness = 5f;

    [Header("Item Handling")]
    [Min(0f)] public float coffeeHoldDuration = 30f;
    [Min(0f)] public float deskCoffeeLifetime = 30f;

    [Header("Obstacle Clearance & Arrival")]
    [Tooltip("Body radius used for pathfinding and physical obstacle checks.")]
    [FormerlySerializedAs("avoidanceRadius")]
    public float navigationRadius = 0.3f;
    [Tooltip("Distance from the action slot where the worker can start acting.")]
    public float slotArriveDistance = 0.16f;

    [Header("Flexible Activity Movement")]
    [Tooltip("Minimum travel distance when choosing an unmarked office position.")]
    [Min(0.25f)] public float freeMoveMinDistance = 1.5f;
    [Tooltip("Maximum travel distance when choosing an unmarked office position.")]
    [Min(0.5f)] public float freeMoveMaxDistance = 6f;
    [Tooltip("How close this worker gets before considering a colleague reached.")]
    [Min(0.4f)] public float colleagueStopDistance = 0.9f;
    [Tooltip("Seconds between path updates while approaching a moving colleague.")]
    [Min(0.1f)] public float movingTargetReplanInterval = 0.45f;
    [Tooltip("Maximum total time spent pursuing a moving colleague.")]
    [Min(2f)] public float movingTargetTimeout = 12f;
    [Tooltip("Minimum visual spacing between workers at free positions.")]
    [Min(0.2f)] public float minimumWorkerSpacing = 0.75f;

    [Header("LLM Social Planning (optional)")]
    [Tooltip("If on, the LLM chooses a partner, subject, and opening before this worker travels to a social point. Ordinary actions continue to use fast utility AI.")]
    public bool useLLMBrain = false;
    [Tooltip("Minimum seconds between generated social plans for this agent.")]
    public float brainDecisionInterval = 20f;
    [Tooltip("How long a planned conversation invitation remains valid.")]
    [Min(15f)] public float brainDirectiveTtl = 45f;
    [Tooltip("If on, the LLM can occasionally choose the next physical office activity. Movement still uses validated pathfinding.")]
    public bool useLLMActivityPlanning = false;
    [Tooltip("Minimum seconds between batch-generation attempts by this agent.")]
    public float activityDecisionInterval = 180f;
    [Tooltip("Generate another batch in the background when this many queued activities remain.")]
    [Range(0, 2)] public int activityPrefetchThreshold = 1;

    private Rigidbody2D rb;
    private OfficeWorkerMotor2D motor;
    private AgentPresentation2D presentation;
    private AgentConversationController conversation;
    private AIWorkerAgent nearbyConversationCaller;
    private float nearbyConversationReservationUntil;
    private AgentEffects effects;
    private AgentEmotionDisplay2D emotionDisplay;
    private AgentNavigationController navigation;
    private OfficeCrowdCoordinator2D crowd;
    private OfficeActionPoint[] actionPoints;
    private OfficeActivityZone[] activityZones;

    private WorkerState state = WorkerState.Thinking;
    private float stateTimer;

    private ConversationIntent conversationIntent;
    private float socialArrivalTime = -1f;
    private bool socialPlanInFlight;
    private bool activityPlanInFlight;
    private readonly Queue<OfficeActivityPlan> plannedActivities = new();
    private OfficeActionPoint pendingSocialPlanAction;
    private string pendingSocialCommitmentSubject;
    private float nextActivityPlanTime;
    private float nextActivityPlanAttemptTime;
    private OfficeActionPoint invitedSocialAction;
    private string invitedBy;
    private float invitationExpiry = -1f;

    private AgentActivity currentActivity;
    private OfficeActionPoint lastFinishedDesk;
    private OfficeActionType? lastFinishedActionType;
    private string lastActionLabel;
    private string activeSequenceId;
    private string activeSequenceObjective;
    private bool stillSeated;
    private bool activityThoughtVisible;
    private Vector2 lastTrackedTargetPosition;
    private float nextTrackedTargetPlanTime;
    private float trackedActivityDeadline;
    private float nextPhoneCallPromptTime;
    private float emotionHoldUntil;
    private AgentEmotion currentEmotion;
    private Vector2 lastLivenessPosition;
    private float lastMovementTime;
    private float stationaryRecoveryDelay;
    private readonly Queue<PreparedBirthdaySurprise> preparedBirthdaySurprises = new();
    private readonly HashSet<string> receivedBirthdaySurprises = new();
    private PreparedBirthdaySurprise activeBirthdaySurprise;
    private PreparedBirthdaySurprise pendingBirthdayThanks;

    public OfficeGrid2D Grid => grid;
    public string AgentId => profile != null && !string.IsNullOrEmpty(profile.AgentId) ? profile.AgentId : name;
    public string DisplayName => profile != null ? profile.DisplayName : AgentId;
    public string AgentType => profile != null && !string.IsNullOrEmpty(profile.AgentType)
        ? profile.AgentType
        : inferredAgentType;
    public string Birthday => profile != null ? profile.Birthday : "";
    public bool UseLLMBrain => useLLMBrain;
    public float SecondsAtSocialPoint => socialArrivalTime < 0f ? 0f : Time.time - socialArrivalTime;
    public bool IsHolding => presentation != null && presentation.IsHolding;
    public AgentEmotion CurrentEmotion => currentEmotion;
    public bool CanJoinStoryBeat => CanAcceptStoryBeat();
    public string CurrentGoal => !string.IsNullOrWhiteSpace(activeSequenceObjective)
        ? activeSequenceObjective
        : currentActivity != null ? GetActionLabel(currentActivity) : lastActionLabel;
    public SceneItemKind HeldItemKind => presentation?.HeldItem != null
        ? presentation.HeldItem.Kind : SceneItemKind.Unknown;
    private bool UsesCentralEpisodeDirector =>
        OfficeEventDirector.Instance != null
        && OfficeEventDirector.Instance.ManagesLLMScenes;

    public string GetConversationSummary()
    {
        return profile != null ? profile.BuildConversationSummary() : DisplayName;
    }

    public string GetEstablishedRelationships()
    {
        return profile != null ? profile.BuildRelationshipSummary() : "";
    }

    public int ScoreConversationTopic(string topic)
    {
        return profile != null ? profile.ScoreTopicRelevance(topic) : 0;
    }

    public string GetLLMStateSummary()
    {
        string schedule = LLMBrainService.Instance != null
            ? LLMBrainService.Instance.ScheduleContext : "unknown time";
        string goal = string.IsNullOrWhiteSpace(CurrentGoal) ? "no active goal" : CurrentGoal;
        return BuildActivityState() + "; current goal: " + goal + "; world time: " + schedule;
    }

    public string GetDirectorStateSummary()
    {
        string activity = state == WorkerState.Moving ? "walking"
            : state == WorkerState.Acting && currentActivity != null
                ? GetActionLabel(currentActivity)
                : "between activities";
        string heldItem = HeldItemKind == SceneItemKind.Snack ? "; carrying a snack"
            : HeldItemKind == SceneItemKind.Coffee ? "; carrying coffee" : "";
        string goal = string.IsNullOrWhiteSpace(CurrentGoal)
            ? "" : "; goal: " + CurrentGoal;
        return currentEmotion.ToString().ToLowerInvariant() + "; " + activity
            + heldItem + goal;
    }

    public ConversationIntent GetConversationIntent(OfficeActionPoint action)
    {
        if (action == null || conversationIntent == null || !conversationIntent.IsValid(Time.time)
            || conversationIntent.actionType != action.actionType)
            return null;

        return conversationIntent;
    }

    public string GetExpectedSocialPartner(OfficeActionPoint action)
    {
        if (action == null || invitedSocialAction != action || Time.time > invitationExpiry
            || string.IsNullOrWhiteSpace(invitedBy))
            return "";
        return invitedBy;
    }

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        motor = GetComponent<OfficeWorkerMotor2D>();
        presentation = GetComponent<AgentPresentation2D>();
        conversation = GetComponent<AgentConversationController>();
        effects = GetComponent<AgentEffects>();
        navigation = GetComponent<AgentNavigationController>();
    }

    private void Start()
    {
        if (grid == null)
            grid = FindFirstObjectByType<OfficeGrid2D>();

        if (string.IsNullOrEmpty(AgentType))
            inferredAgentType = presentation.InferAgentType();

        crowd = OfficeCrowdCoordinator2D.Ensure();
        crowd.Register(this);

        RegisterBrainProfile();
        LLMBrainService.Instance?.RegisterRuntimeAgent(this);
        OfficeEventDirector.Ensure().RegisterWorker(this);

        RefreshActivityEnvironment();

        nextPhoneCallPromptTime = Time.time + Random.Range(45f, 120f);
        emotionDisplay = GetComponent<AgentEmotionDisplay2D>();
        if (emotionDisplay == null)
            emotionDisplay = gameObject.AddComponent<AgentEmotionDisplay2D>();
        emotionDisplay.Bind(this, presentation);
        stateTimer = Random.Range(0.2f, 1f);
        ResetMovementLiveness();
    }

    private void Update()
    {
        effects.TickNeeds(Time.deltaTime);
        TickEmotion();
        TryPrefetchActivityPlans();

        switch (state)
        {
            case WorkerState.Thinking:
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f)
                    DecideNextAction();
                break;

            case WorkerState.Moving:
                TickMoving();
                break;

            case WorkerState.Acting:
                if (TickTrackedActivity())
                    break;
                if (currentActivity != null && currentActivity.waitingForGeneratedSpeech)
                {
                    if (Time.time < currentActivity.generatedSpeechDeadline)
                        break;
                    Debug.LogWarning("[Activity recovered] " + DisplayName
                        + " stopped waiting for generated speech.", this);
                    currentActivity.waitingForGeneratedSpeech = false;
                    stateTimer = Mathf.Min(stateTimer, 0.75f);
                }
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f)
                {
                    FinishAction();
                }
                else
                    conversation.Tick(currentActivity?.actionPoint);
                break;
        }
        UpdatePresentation();
        TickMovementLiveness();
    }

    private void ResetMovementLiveness()
    {
        lastLivenessPosition = GetPosition();
        lastMovementTime = Time.unscaledTime;
        stationaryRecoveryDelay = Random.Range(45f, 75f);
    }

    private void TickMovementLiveness()
    {
        Vector2 position = GetPosition();
        if ((position - lastLivenessPosition).sqrMagnitude >= 0.01f)
        {
            ResetMovementLiveness();
            return;
        }

        if (Time.unscaledTime - lastMovementTime < stationaryRecoveryDelay
            || socialPlanInFlight
            || (conversation != null && conversation.IsInConversation)
            || (currentActivity != null
                && (currentActivity.waitingForGeneratedSpeech
                    || currentActivity.isPreparedBirthdayReaction)))
            return;

        lastMovementTime = Time.unscaledTime;
        stationaryRecoveryDelay = Random.Range(45f, 75f);
        if (state != WorkerState.Thinking)
            InterruptCurrentAction();

        AgentActivity recoveryWalk = new()
        {
            actionType = OfficeActionType.WalkAround,
            destinationMode = OfficeDestinationMode.FreePosition,
            duration = 3f,
            reason = "resume movement after standing too long"
        };
        TryStartFlexibleActivity(recoveryWalk, "General");
    }

    private void OnDestroy()
    {
        if (activePhoneCaller == this)
            activePhoneCaller = null;
        if (LLMBrainService.Instance != null)
        {
            LLMBrainService.Instance.CancelActivityPlanRequest(AgentId);
            LLMBrainService.Instance.UnregisterRuntimeAgent(this);
        }
        if (OfficeCrowdCoordinator2D.Instance != null)
            OfficeCrowdCoordinator2D.Instance.Unregister(this);
        if (OfficeEventDirector.Instance != null)
            OfficeEventDirector.Instance.UnregisterWorker(this);
    }

    public Vector2 GetPosition()
    {
        return rb != null ? rb.position : (Vector2)transform.position;
    }

    public PersistedAgentRuntimeState CaptureRuntimeState()
    {
        Vector2 position = GetPosition();
        PersistedAgentRuntimeState state = new()
        {
            agentId = AgentId,
            positionX = position.x,
            positionY = position.y,
            energy = energy,
            focus = focus,
            social = social,
            productivity = productivity,
            lastAction = currentActivity != null
                ? GetActionLabel(currentActivity) : lastActionLabel,
            activeGoal = activeSequenceObjective,
            capturedAtUnixMilliseconds =
                System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        effects?.CaptureState(state);
        return state;
    }

    public void RestoreRuntimeState(PersistedAgentRuntimeState saved,
        double offlineWorldSeconds)
    {
        if (saved == null)
            return;

        energy = Mathf.Clamp(saved.energy, 0f, 100f);
        focus = Mathf.Clamp(saved.focus, 0f, 100f);
        social = Mathf.Clamp(saved.social, 0f, 100f);
        productivity = Mathf.Max(0f, saved.productivity);
        lastActionLabel = saved.lastAction ?? "";
        activeSequenceObjective = saved.activeGoal ?? "";
        effects?.RestoreState(saved);

        float offlineHours = Mathf.Clamp((float)(offlineWorldSeconds / 3600d), 0f, 8f);
        WorldSchedulePhase phase = LLMBrainService.Instance != null
            ? LLMBrainService.Instance.SchedulePhase : WorldSchedulePhase.Work;
        if (phase == WorldSchedulePhase.Night)
        {
            energy = Mathf.Min(100f, energy + offlineHours * 9f);
            focus = Mathf.Min(100f, focus + offlineHours * 7f);
        }
        else
        {
            energy = Mathf.Max(15f, energy - offlineHours * 5f);
            focus = Mathf.Max(15f, focus - offlineHours * 4f);
            social = Mathf.Max(15f, social - offlineHours * 2f);
        }

        Vector2 savedPosition = new(saved.positionX, saved.positionY);
        if (grid != null && grid.TryFindNearestWalkable(
                savedPosition, Mathf.Max(1f, navigationRadius * 4f), out Vector2 walkable))
            savedPosition = walkable;
        if (rb != null)
            rb.position = savedPosition;
        else
            transform.position = savedPosition;

        // Hand props belong to live interactions and must not survive a restart.
        presentation?.ClearHeldItem();
    }

    public void FaceToward(Vector2 worldPosition)
    {
        if (presentation != null)
            presentation.SetFacing(worldPosition - GetPosition());
    }

    public bool IsActingAt(OfficeActionPoint action)
    {
        return action != null && state == WorkerState.Acting && currentActivity?.actionPoint == action;
    }

    public void RefreshActionPoints()
    {
        actionPoints = FindObjectsOfType<OfficeActionPoint>();
    }

    private void RefreshActivityEnvironment()
    {
        RefreshActionPoints();
        activityZones = FindObjectsOfType<OfficeActivityZone>();
    }

    private void DecideNextAction()
    {
        if (grid == null)
        {
            Debug.LogWarning($"{name}: No OfficeGrid2D assigned.");
            stateTimer = decisionDelay;
            return;
        }

        if (actionPoints == null || actionPoints.Length == 0)
            RefreshActionPoints();

        if (socialPlanInFlight)
        {
            stateTimer = 0.25f;
            return;
        }

        if (TryStartBirthdayThanks() || TryStartPreparedBirthdayReaction())
            return;

        if (!UsesCentralEpisodeDirector && TryStartDuePhoneCall())
            return;

        OfficeActionPoint invitedAction = GetValidInvitation();
        if (invitedAction != null)
        {
            if (TryStartAction(invitedAction))
                return;
            ClearInvitation();
        }

        if (TryStartNextPlannedActivity())
            return;

        TryBeginActivityPlanning();

        OfficeActionPoint bestAction = PickUtilityAction();
        if (bestAction == null)
        {
            stateTimer = decisionDelay;
            return;
        }

        if (AgentConversationController.IsRoutineSocialSpot(bestAction.actionType)
            && GetConversationIntent(bestAction) == null)
        {
            if (UsesCentralEpisodeDirector)
                TryStartAction(bestAction);
            else
                BeginSocialPlanning(bestAction);
            return;
        }

        if (bestAction.actionType == OfficeActionType.PhoneCall)
        {
            if (UsesCentralEpisodeDirector)
            {
                stateTimer = decisionDelay;
                return;
            }
            TryStartFlexibleActivity(new AgentActivity
            {
                actionType = OfficeActionType.PhoneCall,
                destinationMode = OfficeDestinationMode.CurrentPosition,
                duration = bestAction.useTime
            }, "");
            return;
        }

        TryStartAction(bestAction);
    }

    private bool TryStartDuePhoneCall()
    {
        if (UsesCentralEpisodeDirector || Time.time < nextPhoneCallPromptTime
            || (activePhoneCaller != null && activePhoneCaller != this))
            return false;

        AgentActivity call = new()
        {
            actionType = OfficeActionType.PhoneCall,
            destinationMode = OfficeDestinationMode.CurrentPosition,
            duration = 10f,
            reason = BuildSocialState()
        };
        if (!TryStartFlexibleActivity(call, ""))
            return false;

        activePhoneCaller = this;
        return true;
    }

    private void TickEmotion()
    {
        if (currentEmotion == AgentEmotion.Neutral
            || Time.time < emotionHoldUntil)
            return;

        currentEmotion = AgentEmotion.Neutral;
        emotionDisplay?.SetEmotion(currentEmotion);
    }

    public void ReactToDialogue(string line, bool isSpeaker)
    {
        ShowTextEmotion(line, isSpeaker);
    }

    private void ReactToThought(string thought)
    {
        ShowTextEmotion(thought, true);
    }

    private void ShowTextEmotion(string text, bool isSpeaker)
    {
        AgentEmotion? reaction = InferTextEmotion(text, isSpeaker);
        if (!reaction.HasValue)
            return;

        currentEmotion = reaction.Value;
        emotionHoldUntil = Time.time + 6f;
        emotionDisplay?.SetEmotion(currentEmotion);
    }

    private static AgentEmotion? InferTextEmotion(string line, bool isSpeaker)
    {
        string text = (line ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0)
            return null;
        if (ContainsAny(text, "hahaha", "haha", "that's funny", "so funny", "joke"))
            return AgentEmotion.Lol;
        if (ContainsAny(text, "i love you", "go on a date", "my crush", "kiss you",
                "romantic"))
            return isSpeaker ? AgentEmotion.Romantic : AgentEmotion.Surprised;
        if (ContainsAny(text, "furious", "ridiculous", "unacceptable", "annoying",
                "i'm angry", "stop it"))
            return isSpeaker ? AgentEmotion.Angry : AgentEmotion.Shocked;
        if (ContainsAny(text, "heartbroken", "can't stop crying", "devastating",
                "in tears"))
            return AgentEmotion.Crying;
        if (ContainsAny(text, "can't believe", "shocking", "impossible", "what?!"))
            return AgentEmotion.Shocked;
        if (ContainsAny(text, "wow", "no way", "really?", "unexpected", "surprise"))
            return AgentEmotion.Surprised;
        if (ContainsAny(text, "don't understand", "what do you mean", "i'm confused",
                "doesn't make sense"))
            return AgentEmotion.Confused;
        if (ContainsAny(text, "sad", "bad news", "unfortunately", "i'm sorry",
                "i miss"))
            return AgentEmotion.Sad;
        if (ContainsAny(text, "i'm tired", "so tired", "exhausted", "sleepy",
                "can't keep my eyes open", "need some sleep", "yawn"))
            return AgentEmotion.Sleepy;
        if (ContainsAny(text, "awesome", "wonderful", "great", "amazing", "perfect",
                "glad", "happy", "excellent", "sounds good", "love that"))
            return AgentEmotion.Happy;
        if (ContainsAny(text, "deal", "got it", "leave it to me", "handled",
                "we've got this"))
            return AgentEmotion.Cool;
        if (ContainsAny(text, "facepalm", "my mistake", "i forgot",
                "that was embarrassing", "can't believe i forgot", "oops"))
            return AgentEmotion.FacePalm;
        return null;
    }

    private static bool ContainsAny(string text, params string[] fragments)
    {
        foreach (string fragment in fragments)
            if (text.Contains(fragment))
                return true;
        return false;
    }

    private bool CanUseActivityPlanner()
    {
        LLMBrainService brain = LLMBrainService.Instance;
        return !UsesCentralEpisodeDirector
            && useLLMBrain && useLLMActivityPlanning
            && brain != null && brain.EnableActivityPlans
            && Time.time >= nextActivityPlanTime
            && Time.time >= nextActivityPlanAttemptTime;
    }

    private void TryPrefetchActivityPlans()
    {
        if (UsesCentralEpisodeDirector || activityPlanInFlight
            || plannedActivities.Count > Mathf.Clamp(activityPrefetchThreshold, 0, 2))
            return;

        TryBeginActivityPlanning();
    }

    private bool TryBeginActivityPlanning()
    {
        if (activityPlanInFlight || !CanUseActivityPlanner())
            return false;

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || !brain.TryReserveActivityPlanRequest(AgentId))
        {
            nextActivityPlanAttemptTime = Time.time + Random.Range(0.8f, 1.4f);
            return false;
        }

        if (actionPoints == null || actionPoints.Length == 0)
            RefreshActionPoints();

        List<AIWorkerAgent> availableCoworkers = GetAvailableCoworkers();
        List<OfficeActionType> availableTypes = GetAvailableActionTypes(availableCoworkers.Count > 0);
        if (availableTypes.Count == 0)
        {
            nextActivityPlanAttemptTime = Time.time + 2f;
            return false;
        }

        List<ConversationParticipantContext> coworkerContexts = new();
        foreach (AIWorkerAgent coworker in availableCoworkers)
        {
            if (coworker != null && coworker.TryGetComponent(
                    out AgentConversationController coworkerConversation))
                coworkerContexts.Add(coworkerConversation.BuildParticipantContext());
        }

        activityPlanInFlight = true;
        FetchActivityBatchAsync(brain, availableTypes, coworkerContexts);
        return true;
    }

    private async void FetchActivityBatchAsync(
        LLMBrainService brain,
        List<OfficeActionType> availableTypes,
        List<ConversationParticipantContext> coworkerContexts)
    {
        List<OfficeActivityPlan> plans = await brain.PlanActivityBatchAsync(
            AgentId, availableTypes, coworkerContexts, BuildActivityState(), brain.ActivityBatchSize);

        if (this == null)
            return;

        activityPlanInFlight = false;
        nextActivityPlanTime = Time.time + Mathf.Max(5f, activityDecisionInterval);
        if (plans != null)
        {
            OfficeActionType? previousType = LastQueuedActivityType();
            foreach (OfficeActivityPlan plan in plans)
            {
                if (plan == null)
                    continue;
                bool isCommitment = !string.IsNullOrWhiteSpace(plan.socialMemorySubject);
                bool isSequenceContinuation = !string.IsNullOrWhiteSpace(plan.sequenceId) && plan.sequenceStep > 1;
                if (isCommitment && !isSequenceContinuation && HasScheduledCommitment(plan.socialMemorySubject))
                    continue;
                if (!isCommitment && previousType == plan.actionType)
                    continue;
                plannedActivities.Enqueue(plan);
                previousType = plan.actionType;
            }
        }

        if (state == WorkerState.Thinking)
            stateTimer = 0f;
    }

    private OfficeActionType? LastQueuedActivityType()
    {
        OfficeActionType? result = null;
        foreach (OfficeActivityPlan plan in plannedActivities)
            if (plan != null)
                result = plan.actionType;
        return result;
    }

    private bool HasScheduledCommitment(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return false;
        if (currentActivity != null && string.Equals(currentActivity.socialMemorySubject, subject,
                System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(pendingSocialCommitmentSubject, subject,
                System.StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (OfficeActivityPlan queued in plannedActivities)
            if (queued != null && string.Equals(queued.socialMemorySubject, subject,
                    System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private bool TryStartNextPlannedActivity()
    {
        while (plannedActivities.Count > 0)
        {
            OfficeActivityPlan plan = plannedActivities.Dequeue();
            if (TryStartGeneratedActivity(plan))
                return true;
            if (plan != null && plan.isDirected
                && plan.remainingStartAttempts > 1)
            {
                plan.remainingStartAttempts--;
                RequeuePlannedActivityFirst(plan);
                stateTimer = Random.Range(0.6f, 1.1f);
                return false;
            }
            CancelQueuedSequence(plan?.sequenceId);
        }
        return false;
    }

    private void RequeuePlannedActivityFirst(OfficeActivityPlan plan)
    {
        if (plan == null)
            return;
        List<OfficeActivityPlan> remainder = new(plannedActivities);
        plannedActivities.Clear();
        plannedActivities.Enqueue(plan);
        foreach (OfficeActivityPlan queued in remainder)
            plannedActivities.Enqueue(queued);
    }

    private bool TryStartGeneratedActivity(OfficeActivityPlan plan)
    {
        if (plan == null)
            return false;

        bool isCustom = plan.actionType == OfficeActionType.Custom;
        OfficeActionPoint selected = isCustom ? null : PickBestActionOfType(plan.actionType);
        OfficeDestinationMode mode = ResolveDestinationMode(plan, selected);

        if (mode == OfficeDestinationMode.ActionPoint)
        {
            if (selected == null)
            {
                if (plan.isDirected)
                    return false;
                mode = OfficeDestinationMode.FreePosition;
                isCustom = true;
            }
            else
            {
                if (AgentConversationController.IsRoutineSocialSpot(selected.actionType))
                {
                    if (!UsesCentralEpisodeDirector)
                    {
                        BeginSocialPlanning(selected, plan);
                        return true;
                    }
                }
                return TryStartAction(selected, plan);
            }
        }

        AgentActivity activity = new()
        {
            actionType = plan.actionType,
            destinationMode = mode,
            sequenceId = plan.sequenceId,
            sequenceStep = plan.sequenceStep,
            objective = plan.objective,
            duration = Mathf.Clamp(plan.durationSeconds, 2f, 30f),
            reason = plan.reason,
            thought = plan.thought,
            customActionLabel = plan.customActionLabel,
            energyChange = plan.energyChange,
            focusChange = plan.focusChange,
            socialChange = plan.socialChange,
            productivityChange = plan.productivityChange,
            socialMemorySubject = plan.socialMemorySubject,
            completesSocialCommitment = plan.completesSocialCommitment,
            giveHeldItemToTarget = plan.giveHeldItemToTarget
        };

        if (mode == OfficeDestinationMode.FollowAgent)
        {
            activity.targetAgent = FindCoworker(GetAvailableCoworkers(), plan.targetAgent);
            if (activity.targetAgent == null)
                return false;
        }

        return TryStartFlexibleActivity(activity, plan.destinationHint);
    }

    private static OfficeDestinationMode ResolveDestinationMode(
        OfficeActivityPlan plan,
        OfficeActionPoint selected)
    {
        if (plan.actionType == OfficeActionType.Custom)
            return plan.destinationMode == OfficeDestinationMode.CurrentPosition
                ? OfficeDestinationMode.CurrentPosition
                : OfficeDestinationMode.FreePosition;

        switch (plan.actionType)
        {
            case OfficeActionType.ApproachColleague:
                return OfficeDestinationMode.FollowAgent;
            case OfficeActionType.WalkAround:
                return OfficeDestinationMode.FreePosition;
            case OfficeActionType.CheckPhone:
                return OfficeDestinationMode.CurrentPosition;
            case OfficeActionType.Think:
                return plan.destinationMode == OfficeDestinationMode.FreePosition
                    ? OfficeDestinationMode.FreePosition
                    : OfficeDestinationMode.CurrentPosition;
            case OfficeActionType.PhoneCall:
                return OfficeDestinationMode.CurrentPosition;
            default:
                return OfficeDestinationMode.ActionPoint;
        }
    }

    private bool TryStartFlexibleActivity(AgentActivity activity, string destinationHint)
    {
        if (activity == null)
            return false;

        bool conversationApproach = activity.actionType
                == OfficeActionType.ApproachColleague
            && activity.destinationMode == OfficeDestinationMode.FollowAgent;
        if (conversationApproach)
        {
            if (activity.targetAgent == null
                || !activity.targetAgent.CanPauseForNearbyColleague(
                    this, activity.highPriorityConversation))
                return false;
            if (activity.preparedScript == null)
            {
                LLMBrainService brain = LLMBrainService.Instance;
                if (brain == null || !brain.TryReserveRoutineConversation())
                    return false;
                activity.routineConversationReserved = true;
            }
        }

        currentActivity = activity;
        activity.destinationHint = destinationHint;
        stillSeated = false;

        string activityThought = !string.IsNullOrWhiteSpace(activity.thought)
            ? activity.thought : activity.customActionLabel;

        if (activity.destinationMode == OfficeDestinationMode.CurrentPosition)
        {
            activity.destination = GetPosition();
            ShowActivityThought(activityThought);
            StartActing();
            return true;
        }

        if (activity.destinationMode == OfficeDestinationMode.FollowAgent)
        {
            trackedActivityDeadline = Time.time + Mathf.Max(2f, movingTargetTimeout);
            if (!PlanTowardTrackedTarget())
            {
                ClearCurrentActivity();
                return false;
            }

            if (conversationApproach)
                activity.targetAgent.PauseForNearbyColleague(
                    this, Mathf.Max(20f, movingTargetTimeout + 8f));
            else
                ShowActivityThought(activityThought);
            state = WorkerState.Moving;
            return true;
        }

        if (!TryPlanFreeDestination(activity.actionType, destinationHint, out Vector2 destination))
        {
            ClearCurrentActivity();
            return false;
        }

        activity.destination = destination;
        ShowActivityThought(activityThought);
        state = WorkerState.Moving;
        return true;
    }

    private bool TryStartAction(OfficeActionPoint action, OfficeActivityPlan plan = null)
    {
        if (action == null || !action.TryReserve(this))
        {
            stateTimer = decisionDelay;
            return false;
        }

        Vector2 actionDestination = action.GetTargetPosition(this);
        if (crowd != null && !crowd.IsPositionAvailable(
                actionDestination, this, minimumWorkerSpacing))
        {
            action.Release(this);
            stateTimer = decisionDelay;
            return false;
        }

        currentActivity = new AgentActivity
        {
            actionType = action.actionType,
            destinationMode = OfficeDestinationMode.ActionPoint,
            sequenceId = plan != null ? plan.sequenceId : "",
            sequenceStep = plan != null ? plan.sequenceStep : 0,
            objective = plan != null ? plan.objective : "",
            actionPoint = action,
            destination = actionDestination,
            duration = plan != null ? Mathf.Clamp(plan.durationSeconds, 2f, 30f) : action.useTime,
            reason = plan != null ? plan.reason : "",
            thought = plan != null ? plan.thought : "",
            socialMemorySubject = plan != null ? plan.socialMemorySubject : "",
            completesSocialCommitment = plan != null && plan.completesSocialCommitment
        };
        ShowActivityThought(currentActivity.thought);

        if (stillSeated && action == lastFinishedDesk)
        {
            stillSeated = false;
            StartActing();
            return true;
        }

        stillSeated = false;
        if (!navigation.Plan(currentActivity.actionPoint))
        {
            AbortMovement();
            return false;
        }

        state = WorkerState.Moving;
        return true;
    }

    private OfficeActionPoint GetValidInvitation()
    {
        if (invitedSocialAction == null || Time.time > invitationExpiry)
        {
            ClearInvitation();
            return null;
        }
        return invitedSocialAction;
    }

    private void BeginSocialPlanning(OfficeActionPoint action,
        OfficeActivityPlan commitmentPlan = null)
    {
        if (action == null || socialPlanInFlight)
            return;

        List<AIWorkerAgent> candidates = conversation.FindPotentialPartners(action);
        if (candidates.Count == 0)
        {
            stateTimer = decisionDelay;
            return;
        }

        socialPlanInFlight = true;
        pendingSocialPlanAction = action;
        pendingSocialCommitmentSubject = commitmentPlan?.socialMemorySubject;
        stateTimer = 0.25f;
        LLMBrainService brain = LLMBrainService.Instance;

        bool hasCommitment = commitmentPlan != null
            && !string.IsNullOrWhiteSpace(commitmentPlan.socialMemorySubject);
        bool hasPlannedSocialIntent = commitmentPlan != null
            && !string.IsNullOrWhiteSpace(commitmentPlan.targetAgent);
        if (brain == null || !brain.TryReserveRoutineConversation())
        {
            socialPlanInFlight = false;
            pendingSocialPlanAction = null;
            pendingSocialCommitmentSubject = null;
            stateTimer = decisionDelay;
            return;
        }
        bool routineConversationReserved = true;
        float expiresAt = Time.time + Mathf.Max(15f, brainDirectiveTtl);
        ConversationIntent intent = null;
        if (hasCommitment || hasPlannedSocialIntent)
        {
            AIWorkerAgent target = FindCandidate(candidates, commitmentPlan.targetAgent);
            if (target != null)
            {
                string topic = hasCommitment
                    ? commitmentPlan.socialMemorySubject
                    : !string.IsNullOrWhiteSpace(commitmentPlan.objective)
                        ? commitmentPlan.objective : commitmentPlan.reason;
                intent = new ConversationIntent
                {
                    initiatorAgentId = AgentId,
                    initiatorName = DisplayName,
                    intendedPartnerName = target.DisplayName,
                    topic = topic,
                    openingLine = "",
                    generatedByModel = true,
                    actionType = action.actionType,
                    createdAt = Time.time,
                    expiresAt = expiresAt,
                    routineConversationReserved = routineConversationReserved
                };
            }
        }
        if (intent == null && !hasCommitment)
            intent = conversation.CreateLocalConversationIntent(action, expiresAt);
        if (intent != null)
            intent.routineConversationReserved = routineConversationReserved;

        socialPlanInFlight = false;
        pendingSocialPlanAction = null;
        pendingSocialCommitmentSubject = null;
        if (intent == null)
        {
            stateTimer = decisionDelay;
            return;
        }

        conversationIntent = intent;
        AIWorkerAgent invited = FindCandidate(candidates, intent.intendedPartnerName);
        invited?.ReceiveSocialInvitation(action, DisplayName,
            !string.IsNullOrWhiteSpace(commitmentPlan?.companionPreparation)
                ? expiresAt + 30f : expiresAt,
            commitmentPlan?.companionPreparation,
            commitmentPlan?.objective);

        if (!TryStartAction(action, commitmentPlan))
            conversationIntent = null;
    }

    private static AIWorkerAgent FindCandidate(List<AIWorkerAgent> candidates, string displayName)
    {
        if (candidates == null || string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (AIWorkerAgent candidate in candidates)
        {
            if (candidate == null || !string.Equals(candidate.DisplayName, displayName,
                    System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (candidate.TryGetComponent(out AgentConversationController controller)
                && controller.IsInConversation)
                continue;
            return candidate;
        }
        return null;
    }

    private string BuildSocialState()
    {
        string mood = energy < 25f ? "tired"
            : focus < 25f ? "distracted"
            : social < 25f ? "lonely"
            : energy > 75f && focus > 70f ? "energetic"
            : "fairly neutral";
        return mood + (string.IsNullOrWhiteSpace(lastActionLabel)
            ? ""
            : "; just finished: " + lastActionLabel);
    }

    private string BuildActivityState()
    {
        return "energy " + Mathf.RoundToInt(energy) +
            ", focus " + Mathf.RoundToInt(focus) +
            ", social " + Mathf.RoundToInt(social) +
            (Time.time >= nextPhoneCallPromptTime
                ? ", phone call is due"
                : ", phone call not needed yet") +
            (string.IsNullOrWhiteSpace(activeSequenceObjective)
                ? "" : ", continuing objective " + activeSequenceObjective) +
            (string.IsNullOrWhiteSpace(lastActionLabel) ? "" : ", just finished " + lastActionLabel);
    }

    public void ReceiveSocialInvitation(OfficeActionPoint action, string inviterName,
        float expiresAt, string preparation = "", string objective = "",
        bool highPriority = false)
    {
        if (action == null || string.IsNullOrWhiteSpace(inviterName) || expiresAt <= Time.time)
            return;
        if (!highPriority && conversationIntent != null
            && conversationIntent.IsValid(Time.time))
            return;
        if (highPriority)
            conversationIntent = null;

        invitedSocialAction = action;
        invitedBy = inviterName.Trim();
        invitationExpiry = expiresAt;
        if (highPriority)
        {
            if (state == WorkerState.Acting
                && currentActivity?.actionPoint == action)
            {
                conversation?.OnStartedActing(action);
                return;
            }
            InterruptCurrentAction();
            TryStartAction(action);
            return;
        }

        OfficeActionType preparationType;
        SceneItemKind expectedItem;
        if (string.Equals(preparation, "Snack",
                System.StringComparison.OrdinalIgnoreCase))
        {
            preparationType = OfficeActionType.VendingMachine;
            expectedItem = SceneItemKind.Snack;
        }
        else if (string.Equals(preparation, "Coffee",
                System.StringComparison.OrdinalIgnoreCase))
        {
            preparationType = OfficeActionType.CoffeeMachine;
            expectedItem = SceneItemKind.Coffee;
        }
        else
        {
            preparationType = OfficeActionType.Custom;
            expectedItem = SceneItemKind.Unknown;
        }

        bool canPauseToPrepare = state == WorkerState.Thinking
            || (state == WorkerState.Acting && currentActivity != null
                && (currentActivity.actionType == OfficeActionType.WorkDesk
                    || currentActivity.actionType == OfficeActionType.Think
                    || currentActivity.actionType == OfficeActionType.CheckPhone));
        if (expectedItem != SceneItemKind.Unknown && HeldItemKind != expectedItem
            && canPauseToPrepare)
        {
            OfficeActionPoint preparationPoint = PickBestActionOfType(preparationType);
            if (preparationPoint != null)
            {
                if (state != WorkerState.Thinking)
                    InterruptCurrentAction();
                OfficeActivityPlan preparationPlan = new()
                {
                    actionType = preparationType,
                    destinationMode = OfficeDestinationMode.ActionPoint,
                    durationSeconds = 3f,
                    reason = "prepare before meeting " + inviterName,
                    thought = expectedItem == SceneItemKind.Snack
                        ? "I'll grab a snack before we talk."
                        : "I'll grab coffee before we talk.",
                    objective = objective
                };
                if (TryStartAction(preparationPoint, preparationPlan))
                    return;
            }
        }

        if (state == WorkerState.Thinking)
            stateTimer = 0f;
    }

    public bool RequestEventConversation(OfficeActionPoint action, ConversationIntent intent)
    {
        if (action == null || intent == null || !intent.IsValid(Time.time))
            return false;
        if (conversation != null && conversation.IsInConversation)
            return false;

        conversationIntent = intent;
        invitedSocialAction = action;
        invitedBy = intent.initiatorName;
        invitationExpiry = intent.expiresAt;

        if (state == WorkerState.Thinking)
            return TryStartAction(action);

        if (state == WorkerState.Acting && currentActivity?.actionPoint == action)
        {
            conversation.OnStartedActing(currentActivity.actionPoint);
            return true;
        }

        InterruptCurrentAction();
        return TryStartAction(action);
    }

    public bool QueueEpisodeActivity(OfficeActivityPlan plan, bool interruptRoutine)
    {
        if (plan == null)
            return false;

        foreach (OfficeActivityPlan queued in plannedActivities)
            if (queued != null
                && !string.IsNullOrWhiteSpace(plan.sequenceId)
                && string.Equals(queued.sequenceId, plan.sequenceId,
                    System.StringComparison.OrdinalIgnoreCase)
                && queued.sequenceStep == plan.sequenceStep)
                return false;

        bool canInterrupt = interruptRoutine && CanAcceptStoryBeat();
        plannedActivities.Enqueue(plan);
        if (state == WorkerState.Thinking)
            stateTimer = 0f;
        else if (canInterrupt)
            InterruptCurrentAction();
        return true;
    }

    public bool HasEpisodeSequence(string sequenceId)
    {
        if (string.IsNullOrWhiteSpace(sequenceId))
            return false;
        if (currentActivity != null && string.Equals(
                currentActivity.sequenceId, sequenceId,
                System.StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (OfficeActivityPlan queued in plannedActivities)
            if (queued != null && string.Equals(queued.sequenceId, sequenceId,
                    System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public bool StartPreparedPhoneCall(List<string> lines, string reason)
    {
        if (lines == null || lines.Count == 0 || conversation == null
            || conversation.IsInConversation
            || (activePhoneCaller != null && activePhoneCaller != this)
            || !CanAcceptStoryBeat())
            return false;

        InterruptCurrentAction();
        AgentActivity call = new()
        {
            actionType = OfficeActionType.PhoneCall,
            destinationMode = OfficeDestinationMode.CurrentPosition,
            // Keep prepared calls visible long enough to notice at office scale.
            duration = Mathf.Max(14f, lines.Count * 4f),
            reason = reason,
            preparedSpeechLines = new List<string>(lines)
        };
        if (!TryStartFlexibleActivity(call, ""))
            return false;
        activePhoneCaller = this;
        return true;
    }

    public bool RequestApproachConversation(AIWorkerAgent target, string topic,
        string openingLine, bool highPriority = false,
        ConversationScript preparedScript = null)
    {
        if (target == null || target == this || string.IsNullOrWhiteSpace(openingLine)
            || conversation == null || conversation.IsInConversation)
            return false;

        bool protectedCommitment = currentActivity != null
            && !string.IsNullOrWhiteSpace(currentActivity.socialMemorySubject);
        if (protectedCommitment || (!highPriority && !CanAcceptStoryBeat()))
            return false;

        InterruptCurrentAction();
        AgentActivity approach = new()
        {
            actionType = OfficeActionType.ApproachColleague,
            destinationMode = OfficeDestinationMode.FollowAgent,
            targetAgent = target,
            duration = 8f,
            reason = string.IsNullOrWhiteSpace(topic) ? openingLine : topic.Trim(),
            thought = openingLine.Trim(),
            customActionLabel = "talk with " + target.DisplayName,
            highPriorityConversation = highPriority,
            preparedScript = preparedScript
        };

        if (!TryStartFlexibleActivity(approach, ""))
            return false;

        if (!highPriority)
        {
            ShowThought("I need to tell " + target.DisplayName + " something.");
            activityThoughtVisible = true;
        }
        return true;
    }

    public void ReactToWorldEvent(string thought)
    {
        if (string.IsNullOrWhiteSpace(thought) || conversation == null
            || conversation.IsInConversation)
            return;
        ShowThought(thought.Trim());
    }

    private bool CanAcceptStoryBeat()
    {
        if (conversation == null || conversation.IsInConversation || socialPlanInFlight
            || (currentActivity != null
                && !string.IsNullOrWhiteSpace(currentActivity.socialMemorySubject)))
            return false;
        if (state == WorkerState.Thinking)
            return true;
        if (state != WorkerState.Acting || currentActivity == null)
            return false;

        return currentActivity.actionType == OfficeActionType.WorkDesk
            || currentActivity.actionType == OfficeActionType.CheckPhone
            || currentActivity.actionType == OfficeActionType.Think
            || AgentConversationController.IsSocialSpot(
                currentActivity.actionType);
    }

    private void InterruptCurrentAction()
    {
        currentActivity?.actionPoint?.Release(this);
        ClearCurrentActivity();
        stillSeated = false;
        navigation.Clear();
        state = WorkerState.Thinking;
        stateTimer = 0f;
    }

    private void ClearInvitation()
    {
        invitedSocialAction = null;
        invitedBy = "";
        invitationExpiry = -1f;
    }

    private OfficeActionPoint PickUtilityAction()
    {
        OfficeActionPoint bestAction = null;
        float bestScore = float.MinValue;

        foreach (OfficeActionPoint actionPoint in actionPoints)
        {
            if (actionPoint == null)
                continue;
            if (UsesCentralEpisodeDirector
                && actionPoint.actionType == OfficeActionType.PhoneCall)
                continue;

            if (actionPoint.actionType == OfficeActionType.WorkDesk &&
                assignedDesk != null &&
                actionPoint != assignedDesk)
                continue;

            if (actionPoint.IsReservedByOther(this))
                continue;

            float score = ScoreAction(actionPoint);
            score -= EstimateRoutePenalty(actionPoint);

            if (score > bestScore)
            {
                bestScore = score;
                bestAction = actionPoint;
            }
        }

        return bestAction;
    }

    private List<OfficeActionType> GetAvailableActionTypes(bool hasAvailableCoworker)
    {
        List<OfficeActionType> result = new();
        foreach (OfficeActionPoint actionPoint in actionPoints)
        {
            if (actionPoint == null || actionPoint.IsReservedByOther(this))
                continue;
            if (actionPoint.actionType == OfficeActionType.WorkDesk &&
                assignedDesk != null && actionPoint != assignedDesk)
                continue;
            if (!result.Contains(actionPoint.actionType))
                result.Add(actionPoint.actionType);
        }

        if (Time.time >= nextPhoneCallPromptTime)
            AddActivityType(result, OfficeActionType.PhoneCall);
        AddActivityType(result, OfficeActionType.WalkAround);
        AddActivityType(result, OfficeActionType.Think);
        AddActivityType(result, OfficeActionType.CheckPhone);
        AddActivityType(result, OfficeActionType.VendingMachine);
        if (hasAvailableCoworker)
            AddActivityType(result, OfficeActionType.ApproachColleague);
        AddActivityType(result, OfficeActionType.Custom);
        return result;
    }

    private static void AddActivityType(List<OfficeActionType> actions, OfficeActionType actionType)
    {
        if (!actions.Contains(actionType))
            actions.Add(actionType);
    }

    private List<AIWorkerAgent> GetAvailableCoworkers()
    {
        List<AIWorkerAgent> result = new();
        if (crowd == null)
            return result;

        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker == null || worker == this)
                continue;
            if (worker.TryGetComponent(out AgentConversationController controller)
                && controller.IsInConversation)
                continue;
            result.Add(worker);
        }
        return result;
    }

    private static AIWorkerAgent FindCoworker(List<AIWorkerAgent> coworkers, string displayName)
    {
        if (coworkers == null || string.IsNullOrWhiteSpace(displayName))
            return null;

        foreach (AIWorkerAgent coworker in coworkers)
        {
            if (coworker != null
                && (string.Equals(coworker.DisplayName, displayName,
                        System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(coworker.AgentId, displayName,
                        System.StringComparison.OrdinalIgnoreCase))
                && (!coworker.TryGetComponent(out AgentConversationController controller)
                    || !controller.IsInConversation))
                return coworker;
        }
        return null;
    }

    private bool TryPlanFreeDestination(
        OfficeActionType actionType,
        string destinationHint,
        out Vector2 destination)
    {
        if (activityZones == null || activityZones.Length == 0)
            activityZones = FindObjectsOfType<OfficeActivityZone>();

        if (TryPlanZoneDestination(actionType, destinationHint, true, out destination)
            || TryPlanZoneDestination(actionType, destinationHint, false, out destination))
            return true;

        float minDistance = Mathf.Max(0.25f, freeMoveMinDistance);
        float maxDistance = Mathf.Max(minDistance, freeMoveMaxDistance);
        Vector2 origin = GetPosition();
        for (int i = 0; i < 20; i++)
        {
            Vector2 direction = Random.insideUnitCircle.normalized;
            if (direction.sqrMagnitude < 0.1f)
                direction = Vector2.right;
            Vector2 requested = origin + direction * Random.Range(minDistance, maxDistance);
            if (!grid.TryFindNearestWalkable(requested, navigationRadius, out Vector2 candidate))
                continue;
            if (Vector2.Distance(origin, candidate) < minDistance * 0.75f)
                continue;
            if (!IsFreeDestinationAvailable(candidate))
                continue;
            if (!navigation.Plan(candidate))
                continue;

            crowd?.ReserveDestination(this, candidate);
            destination = candidate;
            return true;
        }

        destination = origin;
        return false;
    }

    private bool TryPlanZoneDestination(
        OfficeActionType actionType,
        string destinationHint,
        bool requireHintMatch,
        out Vector2 destination)
    {
        if (activityZones != null)
        {
            int start = activityZones.Length > 0 ? Random.Range(0, activityZones.Length) : 0;
            for (int i = 0; i < activityZones.Length; i++)
            {
                OfficeActivityZone zone = activityZones[(start + i) % activityZones.Length];
                if (zone == null || !zone.Supports(actionType))
                    continue;
                if (requireHintMatch != zone.MatchesHint(destinationHint))
                    continue;
                if (!zone.TrySamplePosition(grid, navigationRadius, out Vector2 candidate)
                    || !IsFreeDestinationAvailable(candidate)
                    || !navigation.Plan(candidate))
                    continue;

                crowd?.ReserveDestination(this, candidate);
                destination = candidate;
                return true;
            }
        }

        destination = GetPosition();
        return false;
    }

    private OfficeActionPoint PickBestActionOfType(OfficeActionType actionType)
    {
        OfficeActionPoint bestAction = null;
        float bestScore = float.MinValue;
        foreach (OfficeActionPoint actionPoint in actionPoints)
        {
            if (actionPoint == null || actionPoint.actionType != actionType)
                continue;
            if (actionPoint.actionType == OfficeActionType.WorkDesk &&
                assignedDesk != null && actionPoint != assignedDesk)
                continue;
            if (actionPoint.IsReservedByOther(this))
                continue;

            float score = ScoreAction(actionPoint) - EstimateRoutePenalty(actionPoint);
            if (score > bestScore)
            {
                bestScore = score;
                bestAction = actionPoint;
            }
        }
        return bestAction;
    }

    private void RegisterBrainProfile()
    {
        LLMBrainService brain = LLMBrainService.Ensure();
        if (brain == null)
            return;

        AgentProfile runtimeProfile = brain.GetProfile(AgentId) ?? new AgentProfile();
        runtimeProfile.agentId = AgentId;
        runtimeProfile.displayName = DisplayName;
        runtimeProfile.personality = profile != null ? profile.BuildPromptDescription() : "";
        runtimeProfile.conversationStyle =
            profile != null ? profile.BuildConversationSummary() : "";
        runtimeProfile.birthday = profile != null ? profile.Birthday : "";
        brain.RegisterProfile(runtimeProfile);

        if (profile == null)
            return;
        foreach (string memory in profile.MemorySeeds)
        {
            if (!string.IsNullOrWhiteSpace(memory))
                brain.Remember(AgentId, memory);
        }
    }

    private static string GetActionLabel(OfficeActionType type)
    {
        switch (type)
        {
            case OfficeActionType.WorkDesk: return "work at your desk";
            case OfficeActionType.CoffeeMachine: return "grab coffee";
            case OfficeActionType.VendingMachine: return "get a snack";
            case OfficeActionType.BreakSpot: return "take a break";
            case OfficeActionType.ChatSpot: return "chat with coworkers";
            case OfficeActionType.MeetingRoom: return "join a meeting";
            case OfficeActionType.PhoneCall: return "make a phone call";
            case OfficeActionType.Printer: return "check the printer";
            case OfficeActionType.Whiteboard: return "think at the whiteboard";
            case OfficeActionType.PlantCare: return "water the office plant";
            case OfficeActionType.WalkAround: return "walk around the office";
            case OfficeActionType.Think: return "pause to think";
            case OfficeActionType.CheckPhone: return "check your phone";
            case OfficeActionType.ApproachColleague: return "approach a colleague";
            default: return type.ToString();
        }
    }

    private string GetActionLabel(AgentActivity activity)
    {
        if (activity == null)
            return "";
        if (!string.IsNullOrWhiteSpace(activity.customActionLabel))
            return activity.customActionLabel;
        return GetActionLabel(activity.actionType);
    }

    private float EstimateRoutePenalty(OfficeActionPoint actionPoint)
    {
        if (grid == null || actionPoint == null)
            return 0f;

        Vector2 target = actionPoint.GetTargetPosition(this);
        float distancePenalty = Vector2.Distance(GetPosition(), target) * 0.35f;
        return distancePenalty;
    }

    private void TickMoving()
    {
        if (currentActivity != null
            && currentActivity.destinationMode == OfficeDestinationMode.FollowAgent)
        {
            if (currentActivity.targetAgent == null || Time.time >= trackedActivityDeadline)
            {
                AbortMovement();
                return;
            }

            float colleagueDistance = Vector2.Distance(
                GetPosition(), currentActivity.targetAgent.GetPosition());
            if (colleagueDistance <= colleagueStopDistance)
            {
                StartActing();
                return;
            }

            Vector2 trackedPosition = currentActivity.targetAgent.GetPosition();
            bool targetMoved = Vector2.Distance(trackedPosition, lastTrackedTargetPosition)
                >= Mathf.Max(0.2f, grid.cellSize * 0.75f);
            if (Time.time >= nextTrackedTargetPlanTime && targetMoved
                && !PlanTowardTrackedTarget())
            {
                AbortMovement();
                return;
            }
        }

        if (MustReplanOccupiedFreeDestination())
        {
            if (!TryPlanFreeDestination(currentActivity.actionType,
                    currentActivity.destinationHint, out Vector2 replacement))
            {
                AbortMovement();
                return;
            }

            currentActivity.destination = replacement;
            return;
        }

        if (currentActivity?.actionPoint != null && crowd != null
            && !crowd.IsPositionAvailable(currentActivity.actionPoint.GetTargetPosition(this),
                this, minimumWorkerSpacing))
        {
            AbortMovement();
            return;
        }

        float movementMultiplier = currentActivity != null
            && currentActivity.destinationMode == OfficeDestinationMode.FollowAgent
            ? 1.15f
            : 1f;
        AgentNavigationController.TickResult result = navigation.Tick(movementMultiplier);
        if (result == AgentNavigationController.TickResult.Arrived)
        {
            if (currentActivity != null
                && currentActivity.destinationMode == OfficeDestinationMode.FollowAgent
                && Vector2.Distance(GetPosition(), currentActivity.targetAgent.GetPosition())
                    > colleagueStopDistance * 1.25f)
            {
                if (!PlanTowardTrackedTarget())
                    AbortMovement();
            }
            else
                StartActing();
        }
        else if (result == AgentNavigationController.TickResult.Failed)
            AbortMovement();
    }

    private bool PlanTowardTrackedTarget()
    {
        if (currentActivity == null || currentActivity.targetAgent == null || grid == null)
            return false;

        Vector2 targetPosition = currentActivity.targetAgent.GetPosition();
        Vector2 approachDirection = GetPosition() - targetPosition;
        if (approachDirection.sqrMagnitude < 0.01f)
            approachDirection = Random.insideUnitCircle;
        if (approachDirection.sqrMagnitude < 0.01f)
            approachDirection = Vector2.right;

        float approachDistance = Mathf.Max(0.55f, colleagueStopDistance * 0.85f);
        Vector2 baseDirection = approachDirection.normalized;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45f * Mathf.Deg2Rad;
            Vector2 direction = new(
                baseDirection.x * Mathf.Cos(angle) - baseDirection.y * Mathf.Sin(angle),
                baseDirection.x * Mathf.Sin(angle) + baseDirection.y * Mathf.Cos(angle));
            Vector2 requested = targetPosition + direction * approachDistance;
            if (!grid.TryFindNearestWalkable(requested, navigationRadius, out Vector2 destination)
                || !IsFreeDestinationAvailable(destination, currentActivity.targetAgent)
                || !navigation.Plan(destination))
                continue;

            crowd?.ReserveDestination(this, destination);
            currentActivity.destination = destination;
            lastTrackedTargetPosition = targetPosition;
            nextTrackedTargetPlanTime = Time.time + Mathf.Max(0.1f, movingTargetReplanInterval);
            return true;
        }
        return false;
    }

    private bool TickTrackedActivity()
    {
        if (currentActivity == null
            || currentActivity.destinationMode != OfficeDestinationMode.FollowAgent)
            return false;

        if (currentActivity.targetAgent == null)
        {
            FinishAction();
            return true;
        }

        if (conversation != null && conversation.IsInConversation)
            return false;

        if (Time.time >= trackedActivityDeadline)
        {
            Debug.Log("[Tracked activity timed out] " + DisplayName + ": "
                + GetActionLabel(currentActivity), this);
            FinishAction();
            return true;
        }

        Vector2 toTarget = currentActivity.targetAgent.GetPosition() - GetPosition();
        presentation.SetFacing(toTarget);
        if (currentActivity.interactionAttempted)
            return false;

        if (toTarget.magnitude <= colleagueStopDistance * 1.45f)
        {
            if (TryBeginNearbyInteraction(currentActivity))
                return true;

            // Do not call across an existing conversation. Wait nearby until the original deadline.
            stateTimer = Mathf.Min(Mathf.Max(stateTimer, 0.5f),
                Mathf.Max(0.5f, trackedActivityDeadline - Time.time));
            currentActivity.waitingForConversationLogged = true;
            return true;
        }

        if (!PlanTowardTrackedTarget())
            return false;

        state = WorkerState.Moving;
        return true;
    }

    private void StartActing()
    {
        crowd?.ClearDestination(this);
        navigation.Clear();
        state = WorkerState.Acting;
        stateTimer = currentActivity != null
            ? Mathf.Max(0.1f, currentActivity.duration)
            : 1f;
        OfficeActionType startedType = currentActivity?.actionType ?? OfficeActionType.Think;
        if (!string.IsNullOrWhiteSpace(currentActivity?.sequenceId))
        {
            activeSequenceId = currentActivity.sequenceId;
            activeSequenceObjective = currentActivity.objective;
        }

        socialArrivalTime = currentActivity?.actionPoint != null && AgentConversationController.IsSocialSpot(currentActivity.actionPoint.actionType)
            ? Time.time
            : -1f;

        // Waiting for someone to chat with: linger longer so they can arrive.
        if (currentActivity?.actionPoint != null && AgentConversationController.IsSocialSpot(currentActivity.actionPoint.actionType)
            && (GetConversationIntent(currentActivity.actionPoint) != null
                || !string.IsNullOrWhiteSpace(GetExpectedSocialPartner(currentActivity.actionPoint))))
        {
            stateTimer = Mathf.Max(stateTimer, 18f);
        }

        if (currentActivity?.actionPoint != null)
            presentation.SetFacing(currentActivity.actionPoint.GetFacingVector(this));
        else if (currentActivity != null && currentActivity.targetAgent != null)
            presentation.SetFacing(currentActivity.targetAgent.GetPosition() - GetPosition());

        if (currentActivity?.actionPoint != null
            && currentActivity.actionPoint.actionType == OfficeActionType.WorkDesk
            && IsHolding)
            PlaceHeldItemOnDesk(currentActivity.actionPoint);

        if (currentActivity != null
            && currentActivity.actionType == OfficeActionType.ApproachColleague
            && currentActivity.targetAgent != null)
        {
            stateTimer = Mathf.Max(stateTimer, 30f);
            TryBeginNearbyInteraction(currentActivity);
        }

        if (currentActivity != null && currentActivity.isPreparedBirthdayReaction
            && activeBirthdaySurprise != null)
        {
            currentActivity.waitingForGeneratedSpeech = true;
            currentActivity.generatedSpeechDeadline = Time.time + 15f;
            BeginBirthdayReactionAsync(currentActivity, activeBirthdaySurprise);
        }

        if (startedType == OfficeActionType.PhoneCall)
        {
            if (currentActivity.preparedSpeechLines != null
                && currentActivity.preparedSpeechLines.Count > 0)
            {
                stateTimer = Mathf.Max(stateTimer,
                    currentActivity.preparedSpeechLines.Count * 2.8f);
                StartCoroutine(DisplayGeneratedLines(currentActivity,
                    currentActivity.preparedSpeechLines, "Phone call",
                    new Color32(37, 99, 235, 255)));
            }
            else if (!UsesCentralEpisodeDirector)
            {
                currentActivity.waitingForGeneratedSpeech = true;
                currentActivity.generatedSpeechDeadline = Time.time + 15f;
                BeginPhoneCallAsync(currentActivity);
            }
        }

        if (currentActivity?.actionPoint != null)
        {
            OfficeActionPoint actionPoint = currentActivity.actionPoint;
            if (actionPoint.actionType == OfficeActionType.CoffeeMachine)
            {
                CoffeeMachine machine = actionPoint.GetComponent<CoffeeMachine>();
                if (machine != null)
                    machine.SpawnRandomCoffeeCup();
            }
            else if (actionPoint.actionType == OfficeActionType.VendingMachine)
            {
                VendingMachine machine = actionPoint.GetComponent<VendingMachine>();
                if (machine != null)
                    machine.SpawnSnack();
            }
            else if (actionPoint.actionType == OfficeActionType.PlantCare)
            {
                ShowThought("taking care of the plant...");
            }
        }

        conversation.OnStartedActing(currentActivity?.actionPoint);
    }

    public void ExtendActing(float seconds)
    {
        if (state == WorkerState.Acting)
            stateTimer += seconds;
    }

    public void EndSocialConversation(bool leaveSoon, float lingerSeconds)
    {
        if (state != WorkerState.Acting || currentActivity?.actionPoint == null
            || !AgentConversationController.IsSocialSpot(currentActivity.actionPoint.actionType))
            return;

        ClearConversationDirective();
        stateTimer = leaveSoon
            ? Mathf.Min(stateTimer, Mathf.Max(0.1f, lingerSeconds))
            : Mathf.Min(stateTimer, Mathf.Max(1f, lingerSeconds));
    }

    public void CompleteConversationActivity(bool succeeded)
    {
        if (state != WorkerState.Acting || currentActivity == null)
            return;
        bool directConversation = currentActivity.actionType
                == OfficeActionType.ApproachColleague
            || (currentActivity.actionType == OfficeActionType.Custom
                && currentActivity.reason != null
                && currentActivity.reason.StartsWith("respond to ",
                    System.StringComparison.OrdinalIgnoreCase));
        if (!directConversation)
            return;

        nearbyConversationCaller = null;
        nearbyConversationReservationUntil = -1f;
        currentActivity.interactionSucceeded = succeeded;
        FinishAction();
    }

    public void EndSilentSocialWait()
    {
        if (state != WorkerState.Acting || currentActivity?.actionPoint == null
            || !AgentConversationController.IsRoutineSocialSpot(
                currentActivity.actionPoint.actionType))
            return;

        ClearConversationDirective();
        stateTimer = Mathf.Min(stateTimer, 0.75f);
    }

    public void ClearConversationDirective()
    {
        conversationIntent = null;
        socialArrivalTime = -1f;
        ClearInvitation();
    }

    public void ShowThought(string content)
    {
        if (presentation == null || string.IsNullOrWhiteSpace(content))
            return;

        string normalized = TextUtils.NormalizeForComparison(content);
        if (normalized.Length == 0)
            return;

        float now = Time.unscaledTime;
        if (RecentThoughtClaims.TryGetValue(normalized, out ThoughtClaim existing)
            && existing.agentId != AgentId
            && now - existing.shownAt < DuplicateThoughtWindowSeconds)
            return;

        RecentThoughtClaims[normalized] = new ThoughtClaim
        {
            agentId = AgentId,
            shownAt = now
        };
        if (RecentThoughtClaims.Count > 64)
            RemoveExpiredThoughtClaims(now);
        presentation.ShowThought(content.Trim());
        ReactToThought(content);
    }

    private static void RemoveExpiredThoughtClaims(float now)
    {
        List<string> expired = new();
        foreach (KeyValuePair<string, ThoughtClaim> pair in RecentThoughtClaims)
            if (pair.Value == null
                || now - pair.Value.shownAt >= DuplicateThoughtWindowSeconds)
                expired.Add(pair.Key);
        foreach (string key in expired)
            RecentThoughtClaims.Remove(key);
    }

    public void HideThought()
    {
        if (presentation != null)
            presentation.HideThought();
    }

    public void ShowSpeech(string speakerName, string content, Color speakerColor)
    {
        if (presentation != null)
            presentation.ShowSpeech(speakerName, content, speakerColor);
    }

    public void HideSpeech()
    {
        if (presentation != null)
            presentation.HideSpeech();
    }

    public void PickupItem(SceneItem item, float holdDuration = 0f)
    {
        if (presentation != null)
        {
            item?.CancelScheduledDestroy();
            presentation.AttachItemToHand(item, default, default, default, holdDuration);
        }
    }

    public void PickupItem(SceneItem item, float holdDuration, Vector3 localScale)
    {
        if (presentation != null)
        {
            item?.CancelScheduledDestroy();
            presentation.AttachItemToHand(item, default, localScale, default, holdDuration);
        }
    }

    public void PlaceHeldItemOnDesk(OfficeActionPoint desk)
    {
        if (presentation != null)
            presentation.PlaceHeldItemAt(desk != null ? desk.GetItemPlacementTransform() : null, Vector3.zero, deskCoffeeLifetime);
    }

    public bool GiveHeldItemTo(AIWorkerAgent recipient)
    {
        if (presentation == null || recipient == null || recipient.presentation == null
            || !presentation.IsHolding || recipient.presentation.IsHolding)
            return false;
        SceneItem item = presentation.ReleaseHeldItem();
        if (item == null)
            return false;
        recipient.PickupItem(item, recipient.coffeeHoldDuration);
        return true;
    }

    private bool TryBeginNearbyInteraction(AgentActivity activity)
    {
        if (activity == null || activity.interactionAttempted || activity.targetAgent == null)
            return false;
        if (Vector2.Distance(GetPosition(), activity.targetAgent.GetPosition())
            > colleagueStopDistance * 1.55f)
            return false;
        if (!activity.targetAgent.CanPauseForNearbyColleague(
                this, activity.highPriorityConversation))
            return false;

        activity.interactionAttempted = true;
        bool needsAttentionCall = activity.preparedScript == null;
        if (needsAttentionCall)
        {
            string call = activity.targetAgent.DisplayName + "!";
            ShowSpeech(DisplayName, call, new Color32(47, 111, 237, 255));
            activityThoughtVisible = true;
        }

        activity.targetAgent.PauseForNearbyColleague(
            this, Mathf.Max(15f, activity.duration + 12f));
        StartCoroutine(BeginApproachConversationAfterGreeting(
            activity, needsAttentionCall));
        return true;
    }

    private IEnumerator BeginApproachConversationAfterGreeting(
        AgentActivity activity, bool showGreeting)
    {
        yield return new WaitForSeconds(showGreeting ? 0.6f : 0.15f);
        if (currentActivity != activity || activity.targetAgent == null)
            yield break;

        AIWorkerAgent target = activity.targetAgent;
        presentation.SetFacing(target.GetPosition() - GetPosition());
        if (showGreeting)
            target.ShowGreetingResponse(DisplayName, GetPosition());

        yield return new WaitForSeconds(showGreeting ? 1.2f : 0.2f);

        if (currentActivity != activity || activity.targetAgent == null)
            yield break;

        if (showGreeting)
            target.HideSpeech();
        string opening = activity.preparedScript?.openingLine ?? "";
        string topic = !string.IsNullOrWhiteSpace(activity.socialMemorySubject)
            ? activity.socialMemorySubject
            : !string.IsNullOrWhiteSpace(activity.reason)
                ? activity.reason : activity.thought;
        bool started = conversation.BeginDirectConversation(
            target, opening, topic, activity.preparedScript,
            activity.routineConversationReserved);
        activity.interactionSucceeded = started;
        if (!started)
        {
            target.EndPausedConversationWith(this);
            CompleteConversationActivity(false);
        }
    }

    public void PauseForNearbyColleague(AIWorkerAgent caller, float seconds)
    {
        if (caller == null || caller == this)
            return;

        nearbyConversationCaller = caller;
        nearbyConversationReservationUntil =
            Time.time + Mathf.Max(3f, seconds);

        currentActivity?.actionPoint?.Release(this);
        navigation.Clear();
        ClearCurrentActivity();
        stillSeated = false;
        currentActivity = new AgentActivity
        {
            actionType = OfficeActionType.Custom,
            destinationMode = OfficeDestinationMode.CurrentPosition,
            duration = Mathf.Max(2f, seconds),
            reason = "respond to " + caller.DisplayName,
            customActionLabel = "talk with " + caller.DisplayName
        };
        state = WorkerState.Acting;
        stateTimer = currentActivity.duration;
        presentation.SetFacing(caller.GetPosition() - GetPosition());
        activityThoughtVisible = true;
    }

    public void EndPausedConversationWith(AIWorkerAgent caller)
    {
        if (caller == null || nearbyConversationCaller != caller)
            return;

        nearbyConversationCaller = null;
        nearbyConversationReservationUntil = -1f;
        bool waitingForCaller = state == WorkerState.Acting
            && currentActivity?.actionType == OfficeActionType.Custom
            && currentActivity.reason != null
            && currentActivity.reason.StartsWith("respond to ",
                System.StringComparison.OrdinalIgnoreCase);
        if (waitingForCaller)
            FinishAction();
    }

    public void ShowGreetingResponse(string callerName, Vector2 callerPosition)
    {
        if (string.IsNullOrWhiteSpace(callerName))
            return;

        presentation.SetFacing(callerPosition - GetPosition());
    }

    public void ReceivePreparedBirthdaySurprise(AIWorkerAgent creator, Vector2 location,
        string description)
    {
        string key = (creator != null ? creator.AgentId : "unknown") + "|"
            + (description ?? "").Trim().ToLowerInvariant();
        if (!receivedBirthdaySurprises.Add(key))
            return;

        preparedBirthdaySurprises.Enqueue(new PreparedBirthdaySurprise
        {
            creator = creator,
            location = location,
            description = description
        });
        Debug.Log("[Birthday surprise noticed] " + DisplayName + " will inspect "
            + (creator != null ? creator.DisplayName + "'s preparation." : "the preparation."), this);
        if (state == WorkerState.Thinking)
            stateTimer = 0f;
    }

    private bool CanPauseForNearbyColleague(AIWorkerAgent caller,
        bool highPriority = false)
    {
        if (caller == null || caller == this || conversation == null
            || conversation.IsInConversation)
            return false;
        if (nearbyConversationCaller != null && nearbyConversationCaller != caller
            && Time.time < nearbyConversationReservationUntil)
            return false;

        bool interruptible = highPriority || state == WorkerState.Thinking
            || currentActivity?.actionPoint == null
            || currentActivity.actionPoint.actionType == OfficeActionType.WorkDesk
            || currentActivity.actionPoint.actionType == OfficeActionType.CheckPhone;
        bool protectedCommitment = currentActivity != null
            && !string.IsNullOrWhiteSpace(currentActivity.socialMemorySubject);
        return interruptible && !protectedCommitment;
    }

    private void FinishAction()
    {
        OfficeActionType? finishedType = currentActivity?.actionType;
        AgentActivity finishedActivity = currentActivity;

        if (currentActivity?.actionPoint != null)
        {
            OfficeActionPoint finishedAction = currentActivity.actionPoint;
            finishedAction.ApplyTo(this);

            if (finishedAction.actionType == OfficeActionType.CoffeeMachine)
            {
                CoffeeMachine machine = finishedAction.GetComponent<CoffeeMachine>();
                SceneItem cup = machine != null ? machine.ConsumeLastCup() : null;
                if (cup != null)
                    PickupItem(cup, coffeeHoldDuration);
            }
            else if (finishedAction.actionType == OfficeActionType.VendingMachine)
            {
                VendingMachine machine = finishedAction.GetComponent<VendingMachine>();
                SceneItem snack = machine != null ? machine.ConsumeLastSnack() : null;
                if (snack != null)
                    PickupItem(snack, coffeeHoldDuration,
                        machine != null ? machine.GetHandScale(snack) : Vector3.one);
            }

            finishedAction.Release(this);

            if (finishedAction.actionType == OfficeActionType.WorkDesk)
            {
                lastFinishedDesk = finishedAction;
                stillSeated = true;

                if (IsHolding)
                    PlaceHeldItemOnDesk(finishedAction);
            }

            if (AgentConversationController.IsSocialSpot(finishedAction.actionType))
                ClearConversationDirective();
        }
        else if (finishedType.HasValue)
            ApplyFlexibleActivityEffects(finishedType.Value);

        if (finishedType.HasValue)
        {
            lastFinishedActionType = finishedType.Value;
            lastActionLabel = GetActionLabel(currentActivity);
            if (finishedType.Value == OfficeActionType.PhoneCall)
                nextPhoneCallPromptTime = Time.time + Random.Range(180f, 360f);
        }

        bool commitmentCompleted = currentActivity != null
            && currentActivity.completesSocialCommitment;
        bool plannedItemTransferred = finishedActivity != null
            && finishedActivity.giveHeldItemToTarget
            && finishedActivity.interactionSucceeded
            && GiveHeldItemTo(finishedActivity.targetAgent);
        if (commitmentCompleted && currentActivity.actionType == OfficeActionType.ApproachColleague)
        {
            string commitmentSubject = currentActivity.socialMemorySubject ?? "";
            bool coffeeDelivery = commitmentSubject.IndexOf("coffee",
                    System.StringComparison.OrdinalIgnoreCase) >= 0
                || commitmentSubject.IndexOf("drink",
                    System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool snackDelivery = commitmentSubject.IndexOf("snack",
                    System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (coffeeDelivery || snackDelivery)
            {
                SceneItemKind expectedKind = snackDelivery
                    ? SceneItemKind.Snack : SceneItemKind.Coffee;
                commitmentCompleted = plannedItemTransferred
                    || (HeldItemKind == expectedKind
                        && GiveHeldItemTo(currentActivity.targetAgent));
            }
            else
            {
                commitmentCompleted = currentActivity.interactionSucceeded;
            }
        }
        if (commitmentCompleted && !string.IsNullOrWhiteSpace(currentActivity.socialMemorySubject))
        {
            Debug.Log("[Commitment fulfilled] " + DisplayName +
                " completed: " + currentActivity.socialMemorySubject, this);
            LLMBrainService.Instance?.CompleteSocialCommitment(
                AgentId, currentActivity.socialMemorySubject);
        }

        if (commitmentCompleted && finishedActivity != null
            && finishedActivity.actionType == OfficeActionType.Custom)
        {
            OfficeEventDirector.Instance?.NotifyBirthdayPreparationCompleted(
                this, GetPosition(), finishedActivity.socialMemorySubject);
        }

        if (finishedActivity != null && finishedActivity.isPreparedBirthdayReaction
            && activeBirthdaySurprise != null)
        {
            if (activeBirthdaySurprise.creator != null
                && !string.IsNullOrWhiteSpace(activeBirthdaySurprise.thankYouLine))
                pendingBirthdayThanks = activeBirthdaySurprise;
            string memory = DisplayName + " found the birthday surprise prepared by "
                + (activeBirthdaySurprise.creator != null
                    ? activeBirthdaySurprise.creator.DisplayName : "someone") + ".";
            LLMBrainService.Instance?.RememberWorldEvent(memory);
            activeBirthdaySurprise = null;
        }

        PhysicalVirtualInteractionBridge bridge = PhysicalVirtualInteractionBridge.Instance;
        if (bridge != null)
            bridge.EvaluateProductivityMilestone();

        string completedSequence = currentActivity?.sequenceId;
        bool sequenceContinues = !string.IsNullOrWhiteSpace(completedSequence)
            && plannedActivities.Count > 0
            && string.Equals(plannedActivities.Peek()?.sequenceId, completedSequence,
                System.StringComparison.OrdinalIgnoreCase);
        if (!sequenceContinues && string.Equals(activeSequenceId, completedSequence,
                System.StringComparison.OrdinalIgnoreCase))
        {
            activeSequenceId = "";
            activeSequenceObjective = "";
        }

        ClearCurrentActivity();
        ReturnToThinking();
        if (sequenceContinues)
            stateTimer = 0.05f;
    }

    private void AbortMovement()
    {
        CancelQueuedSequence(currentActivity?.sequenceId);
        currentActivity?.actionPoint?.Release(this);
        ClearCurrentActivity();
        ReturnToThinking();
    }

    private void ApplyFlexibleActivityEffects(OfficeActionType actionType)
    {
        if (currentActivity != null && (currentActivity.energyChange != 0f
            || currentActivity.focusChange != 0f
            || currentActivity.socialChange != 0f
            || currentActivity.productivityChange != 0f))
        {
            ApplyEffects(
                currentActivity.energyChange,
                currentActivity.focusChange,
                currentActivity.socialChange,
                currentActivity.productivityChange);
            return;
        }

        switch (actionType)
        {
            case OfficeActionType.PhoneCall:
                ApplyEffects(-1f, 1f, 4f, 0f);
                break;
            case OfficeActionType.WalkAround:
                ApplyEffects(-1f, 4f, 0f, 0f);
                break;
            case OfficeActionType.Think:
                ApplyEffects(0f, 5f, 0f, 1f);
                break;
            case OfficeActionType.CheckPhone:
                ApplyEffects(0f, -2f, 1f, 0f);
                break;
            case OfficeActionType.ApproachColleague:
                ApplyEffects(0f, 0f, 4f, 0f);
                break;
        }
    }

    private void ShowActivityThought(string thought)
    {
        if (string.IsNullOrWhiteSpace(thought))
            return;

        ShowThought(thought);
        activityThoughtVisible = true;
    }

    private bool TryStartPreparedBirthdayReaction()
    {
        while (preparedBirthdaySurprises.Count > 0)
        {
            PreparedBirthdaySurprise surprise = preparedBirthdaySurprises.Dequeue();
            if (surprise == null || grid == null)
                continue;

            Vector2 destination;
            if (!grid.TryFindNearestWalkable(surprise.location, navigationRadius, out destination)
                || !navigation.Plan(destination))
                continue;

            activeBirthdaySurprise = surprise;
            currentActivity = new AgentActivity
            {
                actionType = OfficeActionType.Custom,
                destinationMode = OfficeDestinationMode.FreePosition,
                destination = destination,
                duration = 4f,
                reason = surprise.description,
                thought = "Someone prepared a birthday surprise over here.",
                customActionLabel = "inspect birthday surprise",
                isPreparedBirthdayReaction = true
            };
            stillSeated = false;
            crowd?.ReserveDestination(this, destination);
            ShowActivityThought(currentActivity.thought);
            state = WorkerState.Moving;
            return true;
        }
        return false;
    }

    private bool TryStartBirthdayThanks()
    {
        PreparedBirthdaySurprise surprise = pendingBirthdayThanks;
        pendingBirthdayThanks = null;
        AIWorkerAgent creator = surprise?.creator;
        if (creator == null || creator == this)
            return false;

        AgentActivity thanks = new()
        {
            actionType = OfficeActionType.ApproachColleague,
            destinationMode = OfficeDestinationMode.FollowAgent,
            targetAgent = creator,
            duration = 4f,
            reason = "thank " + creator.DisplayName + " for the birthday surprise",
            thought = surprise.thankYouLine,
            customActionLabel = "thank " + creator.DisplayName
        };
        if (TryStartFlexibleActivity(thanks, ""))
            return true;

        return false;
    }

    private async void BeginPhoneCallAsync(AgentActivity activity)
    {
        if (UsesCentralEpisodeDirector)
        {
            if (activity != null)
                activity.waitingForGeneratedSpeech = false;
            return;
        }

        LLMBrainService brain = LLMBrainService.Instance;
        string visibleCoworkers = BuildVisibleCoworkerNameList();
        List<string> lines = brain != null
            ? await brain.GenerateEventMonologueAsync(
                AgentId,
                "Make a natural one-sided personal phone call with someone outside the company. "
                    + "Choose who was called and why. The three lines should progress as one call. "
                    + "Never call, address, or discuss these coworkers who are physically present: "
                    + visibleCoworkers + ".",
                "Current activity: " + (activity?.reason ?? "make a phone call") + ". "
                    + "Current thought: " + (activity?.thought ?? "") + ".",
                3)
            : null;
        if (this == null || currentActivity != activity)
            return;

        activity.waitingForGeneratedSpeech = false;
        if (lines == null || lines.Count == 0 || MentionsVisibleCoworker(lines))
        {
            stateTimer = 1f;
            return;
        }

        stateTimer = Mathf.Max(1f, lines.Count * 2.8f);
        StartCoroutine(DisplayGeneratedLines(activity, lines, "Phone call",
            new Color32(37, 99, 235, 255)));
    }

    private async void BeginBirthdayReactionAsync(AgentActivity activity,
        PreparedBirthdaySurprise surprise)
    {
        string creator = surprise?.creator != null
            ? surprise.creator.DisplayName : "an unknown coworker";
        LLMBrainService brain = LLMBrainService.Instance;
        List<string> lines = brain != null
            ? await brain.GenerateEventMonologueAsync(
                AgentId,
                "React naturally after discovering a birthday surprise. "
                    + "Line one is the private discovery reaction at the location. "
                    + "Line two is what you will later say directly to " + creator
                    + " as thanks. Address " + creator + " in line two.",
                "The surprise was prepared by " + creator + ". Preparation: "
                    + (surprise?.description ?? "birthday surprise") + ".",
                2)
            : null;
        if (this == null || currentActivity != activity)
            return;

        activity.waitingForGeneratedSpeech = false;
        if (lines == null || lines.Count < 2)
            lines = BuildLocalBirthdayReaction(creator);

        surprise.thankYouLine = lines[1];
        stateTimer = 3f;
        StartCoroutine(DisplayGeneratedLines(activity,
            new List<string> { lines[0] }, "Birthday surprise reaction",
            new Color32(181, 83, 9, 255)));
    }

    private string BuildVisibleCoworkerNameList()
    {
        List<string> names = new();
        if (crowd != null)
        {
            foreach (AIWorkerAgent worker in crowd.Workers)
                if (worker != null && worker != this
                    && !string.IsNullOrWhiteSpace(worker.DisplayName))
                    names.Add(worker.DisplayName);
        }
        return names.Count > 0 ? string.Join(", ", names) : "none";
    }

    private bool MentionsVisibleCoworker(List<string> lines)
    {
        if (lines == null || crowd == null)
            return false;
        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker == null || worker == this
                || string.IsNullOrWhiteSpace(worker.DisplayName))
                continue;
            foreach (string line in lines)
                if (!string.IsNullOrWhiteSpace(line)
                    && line.IndexOf(worker.DisplayName,
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
        }
        return false;
    }

    private List<string> BuildLocalBirthdayReaction(string creator)
    {
        string name = string.IsNullOrWhiteSpace(creator) ? "everyone" : creator;
        return new List<string>
        {
            "I genuinely did not expect anyone to prepare this.",
            name + ", thank you. This made the whole day feel different."
        };
    }

    private IEnumerator DisplayGeneratedLines(AgentActivity activity, List<string> lines,
        string logLabel, Color color)
    {
        foreach (string line in lines)
        {
            if (currentActivity != activity)
                yield break;
            ShowSpeech(DisplayName, line, color);
            activityThoughtVisible = true;
            Debug.Log("[" + logLabel + "] " + DisplayName + ": " + line, this);
            yield return new WaitForSeconds(2.7f);
        }
    }

    private void ClearCurrentActivity()
    {
        if (currentActivity?.actionType == OfficeActionType.PhoneCall
            && activePhoneCaller == this)
            activePhoneCaller = null;
        crowd?.ClearDestination(this);
        bool clearedBirthdayReaction = currentActivity != null
            && currentActivity.isPreparedBirthdayReaction;
        currentActivity = null;
        if (clearedBirthdayReaction)
            activeBirthdaySurprise = null;
        trackedActivityDeadline = 0f;
        nextTrackedTargetPlanTime = 0f;
        if (!activityThoughtVisible)
            return;

        HideThought();
        HideSpeech();
        activityThoughtVisible = false;
    }

    private void CancelQueuedSequence(string sequenceId)
    {
        if (string.IsNullOrWhiteSpace(sequenceId))
            return;

        int count = plannedActivities.Count;
        for (int i = 0; i < count; i++)
        {
            OfficeActivityPlan queued = plannedActivities.Dequeue();
            if (queued == null || !string.Equals(queued.sequenceId, sequenceId,
                    System.StringComparison.OrdinalIgnoreCase))
                plannedActivities.Enqueue(queued);
        }

        if (string.Equals(activeSequenceId, sequenceId,
                System.StringComparison.OrdinalIgnoreCase))
        {
            activeSequenceId = "";
            activeSequenceObjective = "";
        }

    }

    private bool IsFreeDestinationAvailable(Vector2 destination,
        AIWorkerAgent ignoredWorker = null)
    {
        float spacing = Mathf.Max(minimumWorkerSpacing, navigationRadius * 2f);
        return crowd == null
            || crowd.IsPositionAvailable(destination, this, spacing, ignoredWorker);
    }

    private bool MustReplanOccupiedFreeDestination()
    {
        if (currentActivity == null
            || currentActivity.destinationMode != OfficeDestinationMode.FreePosition)
            return false;
        if (currentActivity.isPreparedBirthdayReaction)
            return false;

        float checkDistance = Mathf.Max(1f, minimumWorkerSpacing * 1.5f);
        return Vector2.Distance(GetPosition(), currentActivity.destination) <= checkDistance
            && !IsFreeDestinationAvailable(currentActivity.destination);
    }

    private void ReturnToThinking()
    {
        navigation.Clear();
        socialArrivalTime = -1f;
        state = WorkerState.Thinking;
        stateTimer = decisionDelay;
    }

    private float ScoreAction(OfficeActionPoint actionPoint)
    {
        float score = actionPoint.baseScore;
        if (profile != null)
            score += profile.GetActionPreference(actionPoint.actionType);
        score += ScoreScheduleFit(actionPoint.actionType);
        score += Random.Range(0f, randomness * 2f);

        float nEnergy = energy / 100f;
        float nFocus = focus / 100f;
        float nSocial = social / 100f;

        float energyUrgency = Mathf.Pow(1f - nEnergy, 2);
        float focusUrgency = Mathf.Pow(1f - nFocus, 2);
        float socialUrgency = Mathf.Pow(1f - nSocial, 2);

        if (actionPoint.energyChange > 0f) score += actionPoint.energyChange * energyUrgency * 3f;
        if (actionPoint.focusChange > 0f) score += actionPoint.focusChange * focusUrgency * 3f;
        if (actionPoint.socialChange > 0f)
            score += actionPoint.socialChange * socialUrgency * 2.4f;

        if (AgentConversationController.IsRoutineSocialSpot(actionPoint.actionType))
        {
            if (social < 60f)
                score += (60f - social) * 0.65f;
            if (social < 35f)
                score += 18f;
            if (social > 75f)
                score -= (social - 75f) * 0.3f;
            if (conversation != null && conversation.IsSociallyCoolingDown)
                score -= 40f;
            if (!UsesCentralEpisodeDirector
                && !(LLMBrainService.Instance?.CanStartRoutineConversation ?? false))
                score -= 1000f;
        }

        if (actionPoint.CurrentUsers > 0 && actionPoint.actionType == OfficeActionType.ChatSpot)
            score += social < 35f ? 8f : 2f;

        if (actionPoint.actionType == OfficeActionType.VendingMachine)
        {
            float snackNeed = Mathf.Clamp01((100f - energy) / 100f);
            score += 6f + snackNeed * 12f;
            if (presentation != null && presentation.IsHolding)
                score -= 12f;
        }

        if (actionPoint.actionType == OfficeActionType.CoffeeMachine)
        {
            float coffeeNeed = Mathf.Clamp01((100f - energy) / 100f);
            score += 8f + coffeeNeed * 14f;
            if (presentation != null && presentation.IsHolding)
                score -= 10f;
        }

        if (actionPoint.actionType == OfficeActionType.WorkDesk)
        {
            float avgSatisfaction = (nEnergy + nFocus + nSocial) / 3f;
            score += avgSatisfaction * 40f;

            if (energy < 25f) score -= 100f;
            if (focus < 25f) score -= 100f;
            if (social < 15f) score -= 50f;
            if (stillSeated && actionPoint == lastFinishedDesk)
                score -= 90f;
        }

        if (lastFinishedActionType.HasValue
            && lastFinishedActionType.Value == actionPoint.actionType)
            score -= actionPoint.actionType == OfficeActionType.WorkDesk ? 45f : 20f;

        if (actionPoint.actionType == OfficeActionType.PlantCare)
        {
            score += 6f + energyUrgency * 10f + socialUrgency * 5f;
            if (actionPoint.CurrentUsers > 0)
                score -= 15f;
        }

        return score;
    }

    private static float ScoreScheduleFit(OfficeActionType actionType)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return 0f;

        switch (brain.SchedulePhase)
        {
            case WorldSchedulePhase.Morning:
                if (actionType == OfficeActionType.CoffeeMachine) return 22f;
                if (actionType == OfficeActionType.WorkDesk) return 10f;
                break;
            case WorldSchedulePhase.Work:
            case WorldSchedulePhase.Afternoon:
                if (actionType == OfficeActionType.WorkDesk) return 24f;
                if (actionType == OfficeActionType.Whiteboard
                    || actionType == OfficeActionType.Printer) return 12f;
                break;
            case WorldSchedulePhase.Lunch:
                if (actionType == OfficeActionType.BreakSpot
                    || actionType == OfficeActionType.VendingMachine) return 30f;
                if (actionType == OfficeActionType.WorkDesk) return -28f;
                break;
            case WorldSchedulePhase.Evening:
                if (actionType == OfficeActionType.BreakSpot
                    || actionType == OfficeActionType.ChatSpot) return 18f;
                if (actionType == OfficeActionType.WorkDesk) return -12f;
                break;
            case WorldSchedulePhase.Night:
                if (actionType == OfficeActionType.Think
                    || actionType == OfficeActionType.WalkAround
                    || actionType == OfficeActionType.BreakSpot) return 22f;
                if (actionType == OfficeActionType.WorkDesk) return -45f;
                break;
        }
        return 0f;
    }

    public void ApplyEffects(
        float energyChange,
        float focusChange,
        float socialChange,
        float productivityChange)
    {
        effects.Apply(energyChange, focusChange, socialChange, productivityChange);
    }

    public float EffectiveSpeedMultiplier
    {
        get { return effects != null ? effects.EffectiveSpeedMultiplier : 1f; }
    }

    public void ApplySpeedBuff(float multiplier, float duration)
    {
        effects.ApplySpeed(multiplier, duration);
    }

    public void ApplyDecayOverride(float energyMult, float focusMult, float socialMult, float duration)
    {
        effects.ApplyDecayOverride(energyMult, focusMult, socialMult, duration);
    }

    private void UpdatePresentation()
    {
        if (stillSeated && lastFinishedDesk != null)
        {
            Vector2 deskTarget = lastFinishedDesk.GetTargetPosition(this);
            if (Vector2.Distance(GetPosition(), deskTarget) > slotArriveDistance)
                stillSeated = false;
        }

        bool isSitting = stillSeated ||
            (state == WorkerState.Acting && currentActivity?.actionPoint != null && currentActivity.actionPoint.actionType == OfficeActionType.WorkDesk);
        presentation.UpdateState(motor.Velocity, isSitting);
    }
}
