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
    public bool giveHeldItemToTarget;
    public bool interactionAttempted;
    public bool interactionSucceeded;
    public bool highPriorityConversation;
    public ConversationScript preparedScript;
    public bool routineConversationReserved;
    public System.Action onConversationStarted;
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

    [Header("Social Coordination")]
    [Tooltip("How long a planned conversation invitation remains valid.")]
    [Min(15f)] public float brainDirectiveTtl = 45f;

    [Header("Overhearing & Gossip")]
    [Tooltip("Radius in which this worker can overhear a nearby conversation it is not part of.")]
    [Min(0.5f)] public float hearingRadius = 2.6f;
    [Tooltip("Chance to catch a line when accidentally near a conversation that is not private.")]
    [Range(0f, 1f)] public float accidentalHearChance = 0.35f;
    [Tooltip("Chance to catch a private conversation while deliberately listening.")]
    [Range(0f, 1f)] public float purposefulHearChance = 0.9f;
    [Tooltip("Chance an idle worker quietly moves closer to listen, checked once per decision.")]
    [Range(0f, 1f)] public float eavesdropChance = 0.04f;
    [Tooltip("Max distance at which an active conversation can trigger deliberate listening.")]
    [Min(1f)] public float eavesdropTriggerRadius = 5f;
    [Tooltip("Chance a conversation is so private that nobody passing by catches it.")]
    [Range(0f, 1f)] public float conversationSecrecyChance = 0.3f;

    private float hearingMultiplier = 1f;
    private bool deceptiveGossiper;
    private bool jealousNature;

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
    private readonly Queue<OfficeActivityPlan> plannedActivities = new();
    private OfficeActionPoint invitedSocialAction;
    private string invitedBy;
    private float invitationExpiry = -1f;
    private string eavesdroppingConversation;

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
    private float emotionHoldUntil;
    private AgentEmotion currentEmotion;
    private Vector2 lastLivenessPosition;
    private float lastMovementTime;
    private float stationaryRecoveryDelay;

    public OfficeGrid2D Grid => grid;
    public string AgentId => profile != null && !string.IsNullOrEmpty(profile.AgentId) ? profile.AgentId : name;
    public string DisplayName => profile != null ? profile.DisplayName : AgentId;
    public string AgentType => profile != null && !string.IsNullOrEmpty(profile.AgentType)
        ? profile.AgentType
        : inferredAgentType;
    public string Birthday => profile != null ? profile.Birthday : "";
    public float SecondsAtSocialPoint => socialArrivalTime < 0f ? 0f : Time.time - socialArrivalTime;
    public bool IsHolding => presentation != null && presentation.IsHolding;
    public AgentEmotion CurrentEmotion => currentEmotion;
    public bool CanJoinStoryBeat => CanAcceptStoryBeat();
    public string CurrentGoal => !string.IsNullOrWhiteSpace(activeSequenceObjective)
        ? activeSequenceObjective
        : currentActivity != null ? GetActionLabel(currentActivity) : lastActionLabel;
    public SceneItemKind HeldItemKind => presentation?.HeldItem != null
        ? presentation.HeldItem.Kind : SceneItemKind.Unknown;
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

        emotionDisplay = GetComponent<AgentEmotionDisplay2D>();
        if (emotionDisplay == null)
            emotionDisplay = gameObject.AddComponent<AgentEmotionDisplay2D>();
        emotionDisplay.Bind(this, presentation);
        stateTimer = Random.Range(0.2f, 1f);
        ResetMovementLiveness();
        UpdateHearingPersonality();
    }

    private void Update()
    {
        effects.TickNeeds(Time.deltaTime);
        TickEmotion();

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
            || (conversation != null && conversation.IsInConversation))
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
        if (LLMBrainService.Instance != null)
        {
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

        OfficeActionPoint invitedAction = GetValidInvitation();
        if (invitedAction != null)
        {
            if (TryStartAction(invitedAction))
                return;
            ClearInvitation();
        }

        if (TryStartNextPlannedActivity())
            return;

        if (TryStartEavesdropping())
            return;

        OfficeActionPoint bestAction = PickUtilityAction();
        if (bestAction == null)
        {
            stateTimer = decisionDelay;
            return;
        }

        if (AgentConversationController.IsRoutineSocialSpot(bestAction.actionType)
            && GetConversationIntent(bestAction) == null)
        {
            BeginSocialPlanning(bestAction);
            return;
        }

        TryStartAction(bestAction);
    }

    public string EavesdroppingConversation => eavesdroppingConversation;
    public bool IsDeliberatelyListening => !string.IsNullOrEmpty(eavesdroppingConversation);
    public float HearingMultiplier => hearingMultiplier;
    public bool DeceptiveGossiper => deceptiveGossiper;

    private void UpdateHearingPersonality()
    {
        hearingMultiplier = 1f;
        deceptiveGossiper = false;
        jealousNature = false;
        if (profile == null || profile.Traits == null)
            return;
        foreach (string trait in profile.Traits)
        {
            string lower = (trait ?? "").Trim().ToLowerInvariant();
            if (lower.Length == 0)
                continue;
            if (lower.Contains("curious") || lower.Contains("nosy")
                || lower.Contains("inquisitive"))
                hearingMultiplier += 0.5f;
            if (lower.Contains("distracted") || lower.Contains("daydream")
                || lower.Contains("absent-minded"))
                hearingMultiplier = Mathf.Max(0.4f, hearingMultiplier - 0.4f);
            if (lower.Contains("scheming") || lower.Contains("manipulative")
                || lower.Contains("dramatic") || lower.Contains("secretive"))
                deceptiveGossiper = true;
            if (lower.Contains("jealous") || lower.Contains("envious"))
                jealousNature = true;
        }
    }

    private bool TryStartEavesdropping()
    {
        if (conversation != null && conversation.IsInConversation)
            return false;
        if (UnityEngine.Random.value > eavesdropChance * hearingMultiplier)
            return false;

        OfficeConversationHearingRecord target =
            OfficeConversationHearingTracker.FindNearestActive(
                GetPosition(),
                Mathf.Max(eavesdropTriggerRadius, hearingRadius + 2f));
        if (target == null || target.speakerNames == null
            || target.speakerNames.Contains(DisplayName))
            return false;
        if (!TryPlanEavesdropSpot(target, out Vector2 spot))
            return false;

        InterruptCurrentAction();
        eavesdroppingConversation = target.conversationId;
        currentActivity = new AgentActivity
        {
            actionType = OfficeActionType.Eavesdrop,
            destinationMode = OfficeDestinationMode.FreePosition,
            destination = spot,
            duration = UnityEngine.Random.Range(9f, 14f),
            reason = "quietly listen to a nearby conversation"
        };
        crowd?.ReserveDestination(this, spot);
        state = WorkerState.Moving;
        ShowActivityThought("\u201CWhat are they talking about?\u201D");
        return true;
    }

    private bool TryPlanEavesdropSpot(OfficeConversationHearingRecord target,
        out Vector2 spot)
    {
        spot = GetPosition();
        if (grid == null || target == null || crowd == null)
            return false;

        float minSpeakerDistance = Mathf.Max(1f, colleagueStopDistance * 1.1f);
        for (int i = 0; i < 12; i++)
        {
            Vector2 direction = UnityEngine.Random.insideUnitCircle.normalized;
            if (direction.sqrMagnitude < 0.1f)
                direction = Vector2.right;
            float distance = Mathf.Lerp(
                Mathf.Max(0.6f, minSpeakerDistance),
                Mathf.Max(hearingRadius, minSpeakerDistance + 0.4f),
                UnityEngine.Random.value);
            Vector2 requested = target.center + direction * distance;
            if (!grid.TryFindNearestWalkable(
                    requested, navigationRadius, out Vector2 candidate))
                continue;
            bool tooCloseToSpeaker = false;
            foreach (AIWorkerAgent worker in crowd.Workers)
            {
                if (worker == null || worker == this)
                    continue;
                if (target.speakerNames != null
                    && target.speakerNames.Contains(worker.DisplayName)
                    && Vector2.Distance(candidate, worker.GetPosition()) < minSpeakerDistance)
                {
                    tooCloseToSpeaker = true;
                    break;
                }
            }
            if (tooCloseToSpeaker)
                continue;
            if (!IsFreeDestinationAvailable(candidate))
                continue;
            if (!navigation.Plan(candidate))
                continue;

            spot = candidate;
            return true;
        }
        return false;
    }

    public void ReactToHeardGossip(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return;

        ShowThought(subject);
        AgentEmotion? emotion = InferTextEmotion(subject, false);
        if (jealousNature && (emotion == AgentEmotion.Happy
            || emotion == AgentEmotion.Lol
            || emotion == AgentEmotion.Romantic))
        {
            emotion = UnityEngine.Random.value < 0.5f
                ? AgentEmotion.Angry : AgentEmotion.Surprised;
            currentEmotion = emotion.Value;
            emotionHoldUntil = Time.time + 6f;
            emotionDisplay?.SetEmotion(currentEmotion);
        }
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
            && activity.destinationMode == OfficeDestinationMode.FollowAgent
            && !activity.giveHeldItemToTarget;
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
            thought = plan != null ? plan.thought : ""
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

    private void BeginSocialPlanning(OfficeActionPoint action)
    {
        if (action == null)
            return;
        if (!AgentConversationController.HasConversationCapacity)
        {
            stateTimer = Mathf.Max(0.5f, decisionDelay);
            return;
        }

        List<AIWorkerAgent> candidates = conversation.FindPotentialPartners(action);
        if (candidates.Count == 0)
        {
            stateTimer = decisionDelay;
            return;
        }

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || !brain.TryReserveRoutineConversation())
        {
            stateTimer = decisionDelay;
            return;
        }

        float expiresAt = Time.time + Mathf.Max(15f, brainDirectiveTtl);
        ConversationIntent intent =
            conversation.CreateLocalConversationIntent(action, expiresAt);
        if (intent == null)
        {
            stateTimer = decisionDelay;
            return;
        }
        intent.routineConversationReserved = true;

        conversationIntent = intent;
        AIWorkerAgent invited = FindCandidate(candidates, intent.intendedPartnerName);
        invited?.ReceiveSocialInvitation(
            action, DisplayName, expiresAt);

        if (!TryStartAction(action))
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

    private string BuildActivityState()
    {
        string physicalState = energy < 20f ? "exhausted"
            : energy < 40f ? "tired"
            : energy > 80f ? "well rested"
            : "comfortable";
        string attentionState = focus < 25f ? "attention is drifting"
            : focus > 75f ? "deeply focused"
            : "normally focused";
        string socialState = social < 25f ? "wants company"
            : social > 75f ? "socially content"
            : "open to conversation";
        return physicalState + ", " + attentionState + ", " + socialState +
            (string.IsNullOrWhiteSpace(activeSequenceObjective)
                ? "" : ", continuing objective " + activeSequenceObjective) +
            (string.IsNullOrWhiteSpace(lastActionLabel) ? "" : ", just finished " + lastActionLabel);
    }

    public void ReceiveSocialInvitation(OfficeActionPoint action, string inviterName,
        float expiresAt, bool highPriority = false)
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

    public bool HasSequenceWithPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;
        if (currentActivity != null
            && !string.IsNullOrWhiteSpace(currentActivity.sequenceId)
            && currentActivity.sequenceId.StartsWith(prefix,
                System.StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (OfficeActivityPlan queued in plannedActivities)
            if (queued != null && !string.IsNullOrWhiteSpace(queued.sequenceId)
                && queued.sequenceId.StartsWith(prefix,
                    System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public bool RequestApproachConversation(AIWorkerAgent target, string topic,
        string openingLine, bool highPriority = false,
        ConversationScript preparedScript = null,
        System.Action onConversationStarted = null)
    {
        if (target == null || target == this || string.IsNullOrWhiteSpace(openingLine)
            || conversation == null || conversation.IsInConversation)
            return false;

        if (!highPriority && !CanAcceptStoryBeat())
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
            preparedScript = preparedScript,
            onConversationStarted = onConversationStarted
        };

        if (!TryStartFlexibleActivity(approach, ""))
            return false;
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
        if (conversation == null || conversation.IsInConversation)
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
            if (actionPoint == null || !actionPoint.isActiveAndEnabled
                || actionPoint.actionType != actionType)
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
            case OfficeActionType.Printer: return "check the printer";
            case OfficeActionType.Whiteboard: return "think at the whiteboard";
            case OfficeActionType.PlantCare: return "water the office plant";
            case OfficeActionType.WalkAround: return "walk around the office";
            case OfficeActionType.Think: return "pause to think";
            case OfficeActionType.CheckPhone: return "check your phone";
            case OfficeActionType.ApproachColleague: return "approach a colleague";
            case OfficeActionType.Eavesdrop: return "quietly listen in on a conversation";
            case OfficeActionType.InspectPackage: return "inspect the package";
            case OfficeActionType.RepairWifi: return "check the Wi-Fi router";
            case OfficeActionType.Celebrate: return "celebrate with coworkers";
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
        if (startedType == OfficeActionType.Eavesdrop)
        {
            if (string.IsNullOrEmpty(eavesdroppingConversation)
                || !OfficeConversationHearingTracker.IsActive(eavesdroppingConversation))
            {
                eavesdroppingConversation = null;
                stateTimer = Mathf.Min(stateTimer, 3.5f);
            }
        }
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
            float socialDeadline = Time.time + 18f;
            ConversationIntent intent = GetConversationIntent(currentActivity.actionPoint);
            if (intent != null)
                socialDeadline = Mathf.Max(socialDeadline, intent.expiresAt);
            if (invitedSocialAction == currentActivity.actionPoint)
                socialDeadline = Mathf.Max(socialDeadline, invitationExpiry);
            stateTimer = Mathf.Max(stateTimer,
                Mathf.Max(1f, socialDeadline - Time.time + 1f));
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
        ShowPhysicalStoryEmotion(startedType);

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
        }

        conversation.OnStartedActing(currentActivity?.actionPoint);
    }

    private void ShowPhysicalStoryEmotion(OfficeActionType actionType)
    {
        AgentEmotion? emotion = actionType switch
        {
            OfficeActionType.Whiteboard => AgentEmotion.Cool,
            OfficeActionType.InspectPackage => AgentEmotion.Surprised,
            OfficeActionType.RepairWifi => AgentEmotion.Confused,
            OfficeActionType.Celebrate => AgentEmotion.Happy,
            _ => null
        };
        if (!emotion.HasValue)
            return;
        currentEmotion = emotion.Value;
        emotionHoldUntil = Time.time + Mathf.Max(5f, stateTimer);
        emotionDisplay?.SetEmotion(currentEmotion);
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
        if (activity.giveHeldItemToTarget)
        {
            activity.interactionAttempted = true;
            presentation.SetFacing(
                activity.targetAgent.GetPosition() - GetPosition());
            activity.interactionSucceeded =
                GiveHeldItemTo(activity.targetAgent);
            stateTimer = Mathf.Min(stateTimer, 0.75f);
            return true;
        }
        if (!activity.targetAgent.CanPauseForNearbyColleague(
                this, activity.highPriorityConversation))
            return false;

        activity.interactionAttempted = true;
        activity.targetAgent.PauseForNearbyColleague(
            this, Mathf.Max(15f, activity.duration + 12f));
        StartCoroutine(BeginApproachConversation(activity));
        return true;
    }

    private IEnumerator BeginApproachConversation(AgentActivity activity)
    {
        yield return new WaitForSeconds(0.15f);
        if (currentActivity != activity || activity.targetAgent == null)
            yield break;

        AIWorkerAgent target = activity.targetAgent;
        presentation.SetFacing(target.GetPosition() - GetPosition());
        string opening = activity.preparedScript?.openingLine ?? "";
        string topic = !string.IsNullOrWhiteSpace(activity.reason)
            ? activity.reason : activity.thought;
        bool started = conversation.BeginDirectConversation(
            target, opening, topic, activity.preparedScript,
            activity.routineConversationReserved,
            activity.onConversationStarted);
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
        return interruptible;
    }

    private void FinishAction()
    {
        OfficeActionType? finishedType = currentActivity?.actionType;

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
        switch (actionType)
        {
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
            case OfficeActionType.Eavesdrop:
                ApplyEffects(0f, 2f, -1f, 0f);
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

    private void ClearCurrentActivity()
    {
        eavesdroppingConversation = null;
        crowd?.ClearDestination(this);
        currentActivity = null;
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
