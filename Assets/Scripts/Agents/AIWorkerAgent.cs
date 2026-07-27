using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public sealed class ConversationIntent
{
    public string initiatorAgentId;
    public string initiatorName;
    public string intendedPartnerName;
    public string topic;
    public string openingLine;
    public bool generatedByModel;
    public OfficeActionType actionType;
    public float createdAt;
    public float expiresAt;
    public System.Action onOpeningSpoken;

    public bool IsValid(float now)
    {
        return !string.IsNullOrWhiteSpace(openingLine) && now <= expiresAt;
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
    public bool interactionAttempted;
    public bool waitingForConversationLogged;
    public bool interactionSucceeded;
    public bool isPreparedBirthdayReaction;
    public bool waitingForGeneratedSpeech;
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
    public bool useLLMActivityPlanning = true;
    [Tooltip("Minimum seconds between batch-generation attempts by this agent.")]
    public float activityDecisionInterval = 18f;
    [Tooltip("Generate another batch in the background when this many queued activities remain.")]
    [Range(0, 2)] public int activityPrefetchThreshold = 1;

    private Rigidbody2D rb;
    private OfficeWorkerMotor2D motor;
    private AgentPresentation2D presentation;
    private AgentConversationController conversation;
    private AIWorkerAgent nearbyConversationCaller;
    private float nearbyConversationReservationUntil;
    private AgentEffects effects;
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
    private float nextSocialPlanTime;
    private float nextActivityPlanTime;
    private float nextActivityPlanAttemptTime;
    private OfficeActionPoint invitedSocialAction;
    private string invitedBy;
    private float invitationExpiry = -1f;

    private AgentActivity currentActivity;
    private OfficeActionPoint lastFinishedDesk;
    private string lastActionLabel;
    private string activeSequenceId;
    private string activeSequenceObjective;
    private bool stillSeated;
    private bool activityThoughtVisible;
    private Vector2 lastTrackedTargetPosition;
    private float nextTrackedTargetPlanTime;
    private float trackedActivityDeadline;
    private float nextPhoneCallPromptTime;
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
    public int ConversationStarterCount => profile != null ? profile.ConversationStarterCount : 0;
    public bool IsHolding => presentation != null && presentation.IsHolding;
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

    public string GetConversationStarter(string partnerName, int variation)
    {
        return profile != null
            ? profile.GetConversationStarter(partnerName, variation)
            : "Do you have a minute, " + partnerName + "?";
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
        OfficeEventDirector.Ensure().RegisterWorker(this);

        RefreshActivityEnvironment();

        nextPhoneCallPromptTime = Time.time + Random.Range(45f, 120f);
        stateTimer = Random.Range(0.2f, 1f);
    }

    private void Update()
    {
        effects.TickNeeds(Time.deltaTime);
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
    }

    private void OnDestroy()
    {
        if (LLMBrainService.Instance != null)
            LLMBrainService.Instance.CancelActivityPlanRequest(AgentId);
        if (OfficeCrowdCoordinator2D.Instance != null)
            OfficeCrowdCoordinator2D.Instance.Unregister(this);
        if (OfficeEventDirector.Instance != null)
            OfficeEventDirector.Instance.UnregisterWorker(this);
    }

    public Vector2 GetPosition()
    {
        return rb != null ? rb.position : (Vector2)transform.position;
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

        OfficeActionPoint invitedAction = GetValidInvitation();
        if (invitedAction != null)
        {
            TryStartAction(invitedAction);
            return;
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

        if (AgentConversationController.IsSocialSpot(bestAction.actionType)
            && GetConversationIntent(bestAction) == null)
        {
            BeginSocialPlanning(bestAction);
            return;
        }

        TryStartAction(bestAction);
    }

    private bool CanUseActivityPlanner()
    {
        LLMBrainService brain = LLMBrainService.Instance;
        return useLLMBrain && useLLMActivityPlanning && brain != null && brain.EnableActivityPlans
            && Time.time >= nextActivityPlanTime
            && Time.time >= nextActivityPlanAttemptTime;
    }

    private void TryPrefetchActivityPlans()
    {
        if (activityPlanInFlight
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
            CancelQueuedSequence(plan?.sequenceId);
        }
        return false;
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
                mode = OfficeDestinationMode.FreePosition;
                isCustom = true;
            }
            else
            {
                if (AgentConversationController.IsSocialSpot(selected.actionType))
                {
                    BeginSocialPlanning(selected, plan);
                    return true;
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
            completesSocialCommitment = plan.completesSocialCommitment
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
                if (plan.destinationMode == OfficeDestinationMode.ActionPoint && selected != null)
                    return OfficeDestinationMode.ActionPoint;
                return plan.destinationMode == OfficeDestinationMode.CurrentPosition
                    ? OfficeDestinationMode.CurrentPosition
                    : OfficeDestinationMode.FreePosition;
            default:
                return OfficeDestinationMode.ActionPoint;
        }
    }

    private bool TryStartFlexibleActivity(AgentActivity activity, string destinationHint)
    {
        if (activity == null)
            return false;

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

                if (activity.actionType != OfficeActionType.ApproachColleague)
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

    private async void BeginSocialPlanning(OfficeActionPoint action,
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
        ConversationPlan plan = null;
        LLMBrainService brain = LLMBrainService.Instance;

        bool hasCommitment = commitmentPlan != null
            && !string.IsNullOrWhiteSpace(commitmentPlan.socialMemorySubject);
        if (!hasCommitment && useLLMBrain && brain != null && brain.EnableGeneratedConversationPlans
            && Time.time >= nextSocialPlanTime)
        {
            List<ConversationParticipantContext> coworkerContexts = new();
            foreach (AIWorkerAgent candidate in candidates)
            {
                if (candidate != null && candidate.TryGetComponent(
                        out AgentConversationController candidateConversation))
                    coworkerContexts.Add(candidateConversation.BuildParticipantContext());
            }

            plan = await brain.PlanConversationAsync(AgentId, action.actionType,
                coworkerContexts, conversation.BuildRelationships(), BuildSocialState());
            nextSocialPlanTime = Time.time + Mathf.Max(8f, brainDecisionInterval);
        }

        if (this == null)
            return;

        float expiresAt = Time.time + Mathf.Max(15f, brainDirectiveTtl);
        ConversationIntent intent = null;
        if (hasCommitment)
        {
            AIWorkerAgent target = FindCandidate(candidates, commitmentPlan.targetAgent);
            if (target != null)
            {
                intent = new ConversationIntent
                {
                    initiatorAgentId = AgentId,
                    initiatorName = DisplayName,
                    intendedPartnerName = target.DisplayName,
                    topic = commitmentPlan.socialMemorySubject,
                    openingLine = !string.IsNullOrWhiteSpace(commitmentPlan.socialOpeningLine)
                        ? commitmentPlan.socialOpeningLine
                        : target.DisplayName + ", are you still up for what we planned?",
                    generatedByModel = !string.IsNullOrWhiteSpace(
                        commitmentPlan.socialOpeningLine),
                    actionType = action.actionType,
                    createdAt = Time.time,
                    expiresAt = expiresAt
                };
            }
        }
        else if (plan != null)
        {
            AIWorkerAgent target = FindCandidate(candidates, plan.targetAgent);
            if (target != null)
            {
                intent = new ConversationIntent
                {
                    initiatorAgentId = AgentId,
                    initiatorName = DisplayName,
                    intendedPartnerName = target.DisplayName,
                    topic = plan.topic,
                    openingLine = plan.openingLine,
                    generatedByModel = true,
                    actionType = action.actionType,
                    createdAt = Time.time,
                    expiresAt = expiresAt
                };
            }
        }
        if (intent == null && !hasCommitment)
            intent = conversation.CreateLocalConversationIntent(action, expiresAt);

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
        invited?.ReceiveSocialInvitation(action, DisplayName, expiresAt);

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

    public void ReceiveSocialInvitation(OfficeActionPoint action, string inviterName, float expiresAt)
    {
        if (action == null || string.IsNullOrWhiteSpace(inviterName) || expiresAt <= Time.time)
            return;
        if (conversationIntent != null && conversationIntent.IsValid(Time.time))
            return;

        invitedSocialAction = action;
        invitedBy = inviterName.Trim();
        invitationExpiry = expiresAt;
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
            stateTimer = Mathf.Max(stateTimer, 40f);
        }

        if (currentActivity?.actionPoint != null)
            presentation.SetFacing(currentActivity.actionPoint.GetFacingVector(this));
        else if (currentActivity != null && currentActivity.targetAgent != null)
            presentation.SetFacing(currentActivity.targetAgent.GetPosition() - GetPosition());

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
            BeginBirthdayReactionAsync(currentActivity, activeBirthdaySurprise);
        }

        if (startedType == OfficeActionType.PhoneCall)
        {
            currentActivity.waitingForGeneratedSpeech = true;
            BeginPhoneCallAsync(currentActivity);
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

    public void ClearConversationDirective()
    {
        conversationIntent = null;
        socialArrivalTime = -1f;
        ClearInvitation();
    }

    public void ShowThought(string content)
    {
        if (presentation != null)
            presentation.ShowThought(content);
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
        if (!activity.targetAgent.CanPauseForNearbyColleague(this))
            return false;

        activity.interactionAttempted = true;
        string call = activity.targetAgent.DisplayName + "!";
        ShowSpeech(DisplayName, call, new Color32(47, 111, 237, 255));
        activityThoughtVisible = true;

        activity.targetAgent.PauseForNearbyColleague(
            this, Mathf.Max(15f, activity.duration + 12f));
        StartCoroutine(BeginApproachConversationAfterGreeting(activity));
        return true;
    }

    private IEnumerator BeginApproachConversationAfterGreeting(AgentActivity activity)
    {
        yield return new WaitForSeconds(0.6f);
        if (currentActivity != activity || activity.targetAgent == null)
            yield break;

        AIWorkerAgent target = activity.targetAgent;
        presentation.SetFacing(target.GetPosition() - GetPosition());
        target.ShowGreetingResponse(DisplayName, GetPosition());

        yield return new WaitForSeconds(1.2f);

        if (currentActivity != activity || activity.targetAgent == null)
            yield break;

        target.HideSpeech();
        string opening = BuildApproachOpening(activity);
        string topic = !string.IsNullOrWhiteSpace(activity.reason) ? activity.reason : opening;
        bool started = conversation.BeginDirectConversation(target, opening, topic);
        activity.interactionSucceeded = started;
    }

    public void PauseForNearbyColleague(AIWorkerAgent caller, float seconds)
    {
        if (caller == null || caller == this)
            return;

        nearbyConversationCaller = caller;
        nearbyConversationReservationUntil = Time.time + 3f;

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

    public void ShowGreetingResponse(string callerName, Vector2 callerPosition)
    {
        if (string.IsNullOrWhiteSpace(callerName))
            return;

        string response = "Yes, " + callerName + "?";
        ShowSpeech(DisplayName, response, new Color32(47, 133, 90, 255));
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

    private bool CanPauseForNearbyColleague(AIWorkerAgent caller)
    {
        if (caller == null || caller == this || conversation == null
            || conversation.IsInConversation)
            return false;
        if (nearbyConversationCaller != null && nearbyConversationCaller != caller
            && Time.time < nearbyConversationReservationUntil)
            return false;

        bool interruptible = state == WorkerState.Thinking || currentActivity?.actionPoint == null
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
            lastActionLabel = GetActionLabel(currentActivity);
            if (finishedType.Value == OfficeActionType.PhoneCall)
                nextPhoneCallPromptTime = Time.time + Random.Range(240f, 480f);
        }

        bool commitmentCompleted = currentActivity != null
            && currentActivity.completesSocialCommitment;
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
                commitmentCompleted = HeldItemKind == expectedKind
                    && GiveHeldItemTo(currentActivity.targetAgent);
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
        LLMBrainService brain = LLMBrainService.Instance;
        List<string> lines = brain != null
            ? await brain.GenerateEventMonologueAsync(
                AgentId,
                "Make a natural one-sided personal phone call with someone outside the company. "
                    + "Choose who was called and why. The three lines should progress as one call.",
                "Current activity: " + (activity?.reason ?? "make a phone call") + ". "
                    + "Current thought: " + (activity?.thought ?? "") + ".",
                3)
            : null;
        if (this == null || currentActivity != activity)
            return;

        activity.waitingForGeneratedSpeech = false;
        if (lines == null || lines.Count == 0)
        {
            Debug.LogWarning("[Phone call unavailable] " + DisplayName
                + ": the LLM did not return a usable call.", this);
            stateTimer = 0.25f;
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
        {
            Debug.LogWarning("[Birthday reaction unavailable] " + DisplayName
                + ": the LLM did not return a usable reaction.", this);
            stateTimer = 0.25f;
            return;
        }

        surprise.thankYouLine = lines[1];
        stateTimer = 3f;
        StartCoroutine(DisplayGeneratedLines(activity,
            new List<string> { lines[0] }, "Birthday surprise reaction",
            new Color32(181, 83, 9, 255)));
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

    private string BuildApproachOpening(AgentActivity activity)
    {
        string target = activity?.targetAgent != null
            ? activity.targetAgent.DisplayName : "there";
        string subject = activity?.thought?.Trim();
        if (!string.IsNullOrWhiteSpace(activity?.socialMemorySubject)
            && (activity.socialMemorySubject.IndexOf("coffee",
                    System.StringComparison.OrdinalIgnoreCase) >= 0
                || activity.socialMemorySubject.IndexOf("drink",
                    System.StringComparison.OrdinalIgnoreCase) >= 0))
            return target + ", I brought the coffee I promised.";
        if (!string.IsNullOrWhiteSpace(activity?.socialMemorySubject)
            && activity.socialMemorySubject.IndexOf("snack",
                System.StringComparison.OrdinalIgnoreCase) >= 0)
            return target + ", I brought the snack I promised.";

        string reason = activity?.reason?.Trim().TrimEnd('.', '!', '?');
        if (!string.IsNullOrWhiteSpace(reason))
        {
            if (reason.IndexOf("happy birthday", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return target + ", happy birthday!";
            const string advicePrefix = "Ask for advice on ";
            if (reason.StartsWith(advicePrefix, System.StringComparison.OrdinalIgnoreCase))
                return target + ", can I ask your advice about "
                    + reason.Substring(advicePrefix.Length).Trim() + "?";
            const string discussPrefix = "Discuss ";
            if (reason.StartsWith(discussPrefix, System.StringComparison.OrdinalIgnoreCase))
                return target + ", can we talk about "
                    + reason.Substring(discussPrefix.Length).Trim() + "?";
            const string checkPrefix = "Check ";
            if (reason.StartsWith(checkPrefix, System.StringComparison.OrdinalIgnoreCase))
                return target + ", can I ask about "
                    + reason.Substring(checkPrefix.Length).Trim() + "?";
        }

        if (string.IsNullOrWhiteSpace(subject)
            || subject.StartsWith("I should ", System.StringComparison.OrdinalIgnoreCase)
            || subject.StartsWith("I want ", System.StringComparison.OrdinalIgnoreCase)
            || subject.StartsWith("Need to ", System.StringComparison.OrdinalIgnoreCase))
            return target + ", can I ask you something?";
        if (subject.IndexOf(target, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return subject;
        return target + ", " + char.ToLowerInvariant(subject[0]) + subject.Substring(1);
    }

    private void ClearCurrentActivity()
    {
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
        score += Random.Range(0f, randomness * 2f);

        float nEnergy = energy / 100f;
        float nFocus = focus / 100f;
        float nSocial = social / 100f;

        float energyUrgency = Mathf.Pow(1f - nEnergy, 2);
        float focusUrgency = Mathf.Pow(1f - nFocus, 2);
        float socialUrgency = Mathf.Pow(1f - nSocial, 2);

        if (actionPoint.energyChange > 0f) score += actionPoint.energyChange * energyUrgency * 3f;
        if (actionPoint.focusChange > 0f) score += actionPoint.focusChange * focusUrgency * 3f;
        if (actionPoint.socialChange > 0f) score += actionPoint.socialChange * socialUrgency * 1.4f;

        if (AgentConversationController.IsSocialSpot(actionPoint.actionType))
        {
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
        }

        if (actionPoint.actionType == OfficeActionType.PlantCare)
        {
            score += 6f + energyUrgency * 10f + socialUrgency * 5f;
            if (actionPoint.CurrentUsers > 0)
                score -= 15f;
        }

        return score;
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
