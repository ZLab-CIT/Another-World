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

    public bool IsValid(float now)
    {
        return !string.IsNullOrWhiteSpace(openingLine) && now <= expiresAt;
    }
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

    [Header("Obstacle Clearance & Arrival")]
    [Tooltip("Body radius used for pathfinding and physical obstacle checks.")]
    [FormerlySerializedAs("avoidanceRadius")]
    public float navigationRadius = 0.3f;
    [Tooltip("Distance from the action slot where the worker can start acting.")]
    public float slotArriveDistance = 0.16f;

    [Header("LLM Social Planning (optional)")]
    [Tooltip("If on, the LLM chooses a partner, subject, and opening before this worker travels to a social point. Ordinary actions continue to use fast utility AI.")]
    public bool useLLMBrain = false;
    [Tooltip("Minimum seconds between generated social plans for this agent.")]
    public float brainDecisionInterval = 20f;
    [Tooltip("How long a planned conversation invitation remains valid.")]
    [Min(15f)] public float brainDirectiveTtl = 45f;

    private Rigidbody2D rb;
    private OfficeWorkerMotor2D motor;
    private AgentPresentation2D presentation;
    private AgentConversationController conversation;
    private AgentEffects effects;
    private AgentNavigationController navigation;
    private OfficeCrowdCoordinator2D crowd;
    private OfficeActionPoint[] actionPoints;

    private WorkerState state = WorkerState.Thinking;
    private float stateTimer;

    private ConversationIntent conversationIntent;
    private float socialArrivalTime = -1f;
    private bool socialPlanInFlight;
    private OfficeActionPoint pendingSocialPlanAction;
    private float nextSocialPlanTime;
    private OfficeActionPoint invitedSocialAction;
    private string invitedBy;
    private float invitationExpiry = -1f;

    private OfficeActionPoint currentAction;
    private OfficeActionPoint lastFinishedDesk;
    private string lastActionLabel;
    private bool stillSeated;

    public OfficeGrid2D Grid => grid;
    public string AgentId => profile != null && !string.IsNullOrEmpty(profile.AgentId) ? profile.AgentId : name;
    public string DisplayName => profile != null ? profile.DisplayName : AgentId;
    public string AgentType => profile != null && !string.IsNullOrEmpty(profile.AgentType)
        ? profile.AgentType
        : inferredAgentType;
    public bool UseLLMBrain => useLLMBrain;
    public float SecondsAtSocialPoint => socialArrivalTime < 0f ? 0f : Time.time - socialArrivalTime;
    public int ConversationStarterCount => profile != null ? profile.ConversationStarterCount : 0;

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

        RefreshActionPoints();

        stateTimer = Random.Range(0.2f, 1f);
    }

    private void Update()
    {
        effects.TickNeeds(Time.deltaTime);

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
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f)
                {
                    FinishAction();
                }
                else
                    conversation.Tick(currentAction);
                break;
        }
        UpdatePresentation();
    }

    private void OnDestroy()
    {
        if (OfficeCrowdCoordinator2D.Instance != null)
            OfficeCrowdCoordinator2D.Instance.Unregister(this);
    }

    public Vector2 GetPosition()
    {
        return rb != null ? rb.position : (Vector2)transform.position;
    }

    public bool IsActingAt(OfficeActionPoint action)
    {
        return action != null && state == WorkerState.Acting && currentAction == action;
    }

    private void RefreshActionPoints()
    {
        actionPoints = FindObjectsOfType<OfficeActionPoint>();
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

        OfficeActionPoint bestAction = GetValidInvitation();
        bool acceptingInvitation = bestAction != null;
        bestAction ??= PickUtilityAction();

        if (bestAction == null)
        {
            stateTimer = decisionDelay;
            return;
        }

        if (AgentConversationController.IsSocialSpot(bestAction.actionType)
            && !acceptingInvitation
            && GetConversationIntent(bestAction) == null)
        {
            BeginSocialPlanning(bestAction);
            return;
        }

        TryStartAction(bestAction);
    }

    private bool TryStartAction(OfficeActionPoint action)
    {
        if (action == null || !action.TryReserve(this))
        {
            stateTimer = decisionDelay;
            return false;
        }

        currentAction = action;

        if (stillSeated && action == lastFinishedDesk)
        {
            stillSeated = false;
            StartActing();
            return true;
        }

        stillSeated = false;
        if (!navigation.Plan(currentAction))
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

    private async void BeginSocialPlanning(OfficeActionPoint action)
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
        stateTimer = 0.25f;
        ConversationPlan plan = null;
        LLMBrainService brain = LLMBrainService.Instance;

        if (useLLMBrain && brain != null && brain.EnableGeneratedConversationPlans
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
        if (plan != null)
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
        intent ??= conversation.CreateLocalConversationIntent(action, expiresAt);

        socialPlanInFlight = false;
        pendingSocialPlanAction = null;
        if (intent == null)
        {
            stateTimer = decisionDelay;
            return;
        }

        conversationIntent = intent;
        AIWorkerAgent invited = FindCandidate(candidates, intent.intendedPartnerName);
        invited?.ReceiveSocialInvitation(action, DisplayName, expiresAt);

        string source = intent.generatedByModel ? "AI" : "local";
        Debug.Log("[Conversation intent - " + source + "] " + DisplayName + " -> " +
            intent.intendedPartnerName + ": " + intent.topic, this);

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
            case OfficeActionType.BreakSpot: return "take a break";
            case OfficeActionType.ChatSpot: return "chat with coworkers";
            case OfficeActionType.MeetingRoom: return "join a meeting";
            default: return type.ToString();
        }
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
        AgentNavigationController.TickResult result = navigation.Tick();
        if (result == AgentNavigationController.TickResult.Arrived)
            StartActing();
        else if (result == AgentNavigationController.TickResult.Failed)
            AbortMovement();
    }
    private void StartActing()
    {
        navigation.Clear();
        state = WorkerState.Acting;
        stateTimer = currentAction != null ? currentAction.useTime : 1f;
        socialArrivalTime = currentAction != null && AgentConversationController.IsSocialSpot(currentAction.actionType)
            ? Time.time
            : -1f;

        // Waiting for someone to chat with: linger longer so they can arrive.
        if (currentAction != null && AgentConversationController.IsSocialSpot(currentAction.actionType)
            && (GetConversationIntent(currentAction) != null
                || !string.IsNullOrWhiteSpace(GetExpectedSocialPartner(currentAction))))
        {
            stateTimer = Mathf.Max(stateTimer, 40f);
        }

        if (currentAction != null)
            presentation.SetFacing(currentAction.GetFacingVector(this));

        conversation.OnStartedActing(currentAction);
    }

    public void ExtendActing(float seconds)
    {
        if (state == WorkerState.Acting)
            stateTimer += seconds;
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

    private void FinishAction()
    {
        if (currentAction != null)
        {
            OfficeActionPoint finishedAction = currentAction;
            currentAction.ApplyTo(this);
            currentAction.Release(this);

            if (finishedAction.actionType == OfficeActionType.WorkDesk)
            {
                lastFinishedDesk = finishedAction;
                stillSeated = true;
            }

            lastActionLabel = GetActionLabel(finishedAction.actionType);
            if (AgentConversationController.IsSocialSpot(finishedAction.actionType))
                ClearConversationDirective();

            PhysicalVirtualInteractionBridge bridge = PhysicalVirtualInteractionBridge.Instance;
            if (bridge != null)
                bridge.EvaluateProductivityMilestone();

            currentAction = null;
        }

        ReturnToThinking();
    }

    private void AbortMovement()
    {
        if (currentAction != null)
        {
            currentAction.Release(this);
            currentAction = null;
        }

        ReturnToThinking();
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
        if (actionPoint.socialChange > 0f) score += actionPoint.socialChange * socialUrgency * 3f;

        if (actionPoint.CurrentUsers > 0 && actionPoint.actionType == OfficeActionType.ChatSpot)
            score += 12f;

        if (actionPoint.actionType == OfficeActionType.WorkDesk)
        {
            float avgSatisfaction = (nEnergy + nFocus + nSocial) / 3f;
            score += avgSatisfaction * 40f;

            if (energy < 25f) score -= 100f;
            if (focus < 25f) score -= 100f;
            if (social < 15f) score -= 50f;
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
            (state == WorkerState.Acting && currentAction != null && currentAction.actionType == OfficeActionType.WorkDesk);
        presentation.UpdateState(motor.Velocity, isSitting);
    }
}
