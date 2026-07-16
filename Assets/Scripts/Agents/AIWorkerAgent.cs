using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(OfficeWorkerMotor2D))]
[RequireComponent(typeof(AgentPresentation2D))]
[RequireComponent(typeof(AgentConversationController))]
[RequireComponent(typeof(AgentNavigationController))]
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

    [Header("LLM Brain (optional)")]
    [Tooltip("If on, the agent periodically asks the LLM what to do, guided by its personality. Falls back to utility scoring when the LLM is unavailable or slow.")]
    public bool useLLMBrain = false;
    [Tooltip("Minimum seconds between LLM queries for this agent.")]
    public float brainDecisionInterval = 20f;
    [Tooltip("How long an LLM-chosen directive stays valid before reverting to utility scoring.")]
    public float brainDirectiveTtl = 30f;
    [Tooltip("Log each LLM decision and reason to the console for tuning.")]
    [SerializeField] private bool logBrainDecisions = false;
    [Tooltip("Show private LLM decision reasons for non-social actions. Keep off if you only want spoken dialogue.")]
    public bool showDecisionThoughtBubbles = false;

    private Rigidbody2D rb;
    private OfficeWorkerMotor2D motor;
    private AgentPresentation2D presentation;
    private AgentConversationController conversation;
    private AgentNavigationController navigation;
    private OfficeCrowdCoordinator2D crowd;
    private OfficeActionPoint[] actionPoints;

    private WorkerState state = WorkerState.Thinking;
    private float stateTimer;

    private string brainDirectiveActionId;
    private string brainDirectiveTargetAgent;
    private string brainDirectiveReason;
    private string pendingBrainThought;
    private float brainDirectiveExpiry = -1f;
    private float nextBrainQueryTime;
    private bool brainQueryInFlight;

    private OfficeActionPoint currentAction;
    private OfficeActionPoint lastFinishedDesk;
    private string lastActionLabel;
    private bool stillSeated;

    private float speedBuffMultiplier = 1f;
    private float speedBuffUntil = -1f;
    private float energyDecayMult = 1f;
    private float focusDecayMult = 1f;
    private float socialDecayMult = 1f;
    private float decayOverrideUntil = -1f;

    public OfficeGrid2D Grid => grid;
    public string AgentId => profile != null && !string.IsNullOrEmpty(profile.AgentId) ? profile.AgentId : name;
    public string DisplayName => profile != null ? profile.DisplayName : AgentId;
    public string AgentType => profile != null && !string.IsNullOrEmpty(profile.AgentType)
        ? profile.AgentType
        : inferredAgentType;
    public bool UseLLMBrain => useLLMBrain;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        motor = GetComponent<OfficeWorkerMotor2D>();
        presentation = GetComponent<AgentPresentation2D>();
        conversation = GetComponent<AgentConversationController>();
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
        TickNeeds(Time.deltaTime);

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
                    conversation.Tick(currentAction, brainDirectiveReason);
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
    private void TickNeeds(float deltaTime)
    {
        bool decayOverridden = Time.time < decayOverrideUntil;
        float energyRate = decayOverridden ? energyDecay * energyDecayMult : energyDecay;
        float focusRate = decayOverridden ? focusDecay * focusDecayMult : focusDecay;
        float socialRate = decayOverridden ? socialDecay * socialDecayMult : socialDecay;
        energy = Mathf.Clamp(energy - energyRate * deltaTime, 0f, 100f);
        focus = Mathf.Clamp(focus - focusRate * deltaTime, 0f, 100f);
        social = Mathf.Clamp(social - socialRate * deltaTime, 0f, 100f);
    }

    private void RefreshActionPoints()
    {
#if UNITY_2023_1_OR_NEWER
        actionPoints = FindObjectsByType<OfficeActionPoint>(FindObjectsSortMode.None);
#else
        actionPoints = FindObjectsOfType<OfficeActionPoint>();
#endif
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

        MaybeQueryBrain();

        pendingBrainThought = null;
        OfficeActionPoint bestAction = PickBrainAction();
        if (bestAction == null)
            bestAction = PickUtilityAction();

        if (bestAction == null || !bestAction.TryReserve(this))
        {
            stateTimer = decisionDelay;
            return;
        }

        currentAction = bestAction;

        if (stillSeated && bestAction == lastFinishedDesk)
        {
            stillSeated = false;
            StartActing();
            return;
        }

        stillSeated = false;
        if (!navigation.Plan(currentAction))
        {
            AbortMovement();
            return;
        }

        state = WorkerState.Moving;
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

    private OfficeActionPoint PickBrainAction()
    {
        if (!useLLMBrain)
            return null;

        if (string.IsNullOrEmpty(brainDirectiveActionId) || Time.time > brainDirectiveExpiry)
            return null;

        if (!System.Enum.TryParse(brainDirectiveActionId, true, out OfficeActionType desiredType))
            return null;

        OfficeActionPoint result = null;

        if (desiredType == OfficeActionType.WorkDesk && assignedDesk != null)
        {
            if (!assignedDesk.IsReservedByOther(this))
                result = assignedDesk;
        }
        else
        {
            foreach (OfficeActionPoint actionPoint in actionPoints)
            {
                if (actionPoint == null || actionPoint.actionType != desiredType)
                    continue;

                if (actionPoint.IsReservedByOther(this))
                    continue;

                result = actionPoint;
                break;
            }
        }

        if (result != null)
        {
            pendingBrainThought = brainDirectiveReason;

            // If heading to chat with a specific coworker, nudge them to come too
            // so the two actually meet at the ChatSpot. Only genuine LLM decisions
            // nudge (a nudge carries no line, so it won't recurse).
            if (desiredType == OfficeActionType.ChatSpot
                && !string.IsNullOrEmpty(brainDirectiveReason)
                && !string.IsNullOrEmpty(brainDirectiveTargetAgent))
            {
                AIWorkerAgent named = conversation.FindWorkerByDisplayName(brainDirectiveTargetAgent);
                if (named != null && named != this)
                    named.NudgeToChat(DisplayName, 18f);
            }
        }

        return result;
    }

    private async void MaybeQueryBrain()
    {
        if (!useLLMBrain || brainQueryInFlight || Time.time < nextBrainQueryTime)
            return;

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return;

        brainQueryInFlight = true;
        nextBrainQueryTime = Time.time + brainDecisionInterval;

        AgentStateSnapshot snapshot = new()
        {
            energy = energy,
            focus = focus,
            social = social,
            productivity = productivity,
            mood = DeriveMood(),
            lastAction = lastActionLabel ?? "",
            relationships = conversation.BuildRelationships(),
            coworkers = BuildCoworkerList()
        };

        List<ActionOption> options = BuildActionOptions();

        try
        {
            AgentDecision decision = await brain.ChooseActionAsync(AgentId, snapshot, options);
            if (decision != null && !string.IsNullOrEmpty(decision.actionId))
            {
                brainDirectiveActionId = decision.actionId;
                brainDirectiveTargetAgent = decision.targetAgent ?? "";
                brainDirectiveReason = decision.reason ?? "";
                brainDirectiveExpiry = Time.time + brainDirectiveTtl;

                if (logBrainDecisions)
                {
                    string with = string.IsNullOrEmpty(decision.targetAgent) ? "" : " with " + decision.targetAgent;
                    string because = string.IsNullOrEmpty(decision.reason) ? "" : " — " + decision.reason;
                    Debug.Log($"[{DisplayName}] LLM chose {decision.actionId}{with}{because}");
                }
            }
        }
        catch
        {
            // Swallow: utility scoring remains the fallback.
        }
        finally
        {
            brainQueryInFlight = false;
        }
    }

    private void RegisterBrainProfile()
    {
        LLMBrainService brain = LLMBrainService.Ensure();
        if (brain == null || brain.GetProfile(AgentId) != null)
            return;

        brain.RegisterProfile(new AgentProfile
        {
            agentId = AgentId,
            displayName = DisplayName,
            personality = profile != null ? profile.BuildPromptDescription() : ""
        });
    }

    private string DeriveMood()
    {
        if (energy < 25f) return "tired";
        if (focus < 25f) return "unfocused";
        if (social < 25f) return "lonely";
        if (energy > 75f && focus > 75f && social > 60f) return "content";
        return "neutral";
    }

    private string BuildCoworkerList()
    {
        if (crowd == null || crowd.Workers.Count <= 1)
            return "";

        string result = "";
        for (int i = 0; i < crowd.Workers.Count; i++)
        {
            AIWorkerAgent worker = crowd.Workers[i];
            if (worker == null || worker == this)
                continue;

            if (result.Length > 0)
                result += ", ";
            result += worker.DisplayName;
        }

        return result;
    }

    private List<ActionOption> BuildActionOptions()
    {
        List<ActionOption> options = new();
        HashSet<OfficeActionType> seen = new();

        if (actionPoints != null)
        {
            foreach (OfficeActionPoint actionPoint in actionPoints)
            {
                if (actionPoint == null || !seen.Add(actionPoint.actionType))
                    continue;

                options.Add(new ActionOption
                {
                    actionId = actionPoint.actionType.ToString(),
                    label = GetActionLabel(actionPoint.actionType)
                });
            }
        }

        return options;
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

        // Waiting for someone to chat with: linger longer so they can arrive.
        if (currentAction != null && AgentConversationController.IsSocialSpot(currentAction.actionType)
            && !string.IsNullOrEmpty(brainDirectiveReason))
        {
            stateTimer = Mathf.Max(stateTimer, 20f);
        }

        if (currentAction != null)
            presentation.SetFacing(currentAction.GetFacingVector(this));

        if (presentation.ThoughtBubblesEnabled && showDecisionThoughtBubbles && !string.IsNullOrEmpty(pendingBrainThought)
            && currentAction != null && !AgentConversationController.IsSocialSpot(currentAction.actionType))
        {
            string label = GetActionLabel(currentAction.actionType);
            presentation.ShowThought($"{DisplayName} · {label}\n{pendingBrainThought}");
        }
        pendingBrainThought = null;

        conversation.OnStartedActing(currentAction, brainDirectiveReason);
    }

    public void NudgeToChat(string partnerName, float ttl)
    {
        brainDirectiveActionId = OfficeActionType.ChatSpot.ToString();
        brainDirectiveTargetAgent = partnerName ?? "";
        brainDirectiveReason = "";
        brainDirectiveExpiry = Time.time + ttl;
    }

    public void ExtendActing(float seconds)
    {
        if (state == WorkerState.Acting)
            stateTimer += seconds;
    }

    public void ClearConversationDirective()
    {
        brainDirectiveTargetAgent = "";
        brainDirectiveReason = "";
        brainDirectiveActionId = "";
    }
    public void ShowThought(string content)
    {
        presentation.ShowThought(content);
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

        pendingBrainThought = null;

        ReturnToThinking();
    }

    private void ReturnToThinking()
    {
        navigation.Clear();
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
            score += 40f;

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
        energy = Mathf.Clamp(energy + energyChange, 0f, 100f);
        focus = Mathf.Clamp(focus + focusChange, 0f, 100f);
        social = Mathf.Clamp(social + socialChange, 0f, 100f);
        productivity = Mathf.Max(0f, productivity + productivityChange);

        PhysicalVirtualInteractionBridge bridge = PhysicalVirtualInteractionBridge.Instance;
        if (bridge != null)
            bridge.EvaluateProductivityMilestone();
    }

    public float EffectiveSpeedMultiplier
    {
        get { return Time.time < speedBuffUntil ? speedBuffMultiplier : 1f; }
    }

    public void ApplySpeedBuff(float multiplier, float duration)
    {
        if (duration <= 0f)
            return;

        speedBuffMultiplier = Mathf.Max(0f, multiplier);
        speedBuffUntil = Time.time + duration;
    }

    public void ApplyDecayOverride(float energyMult, float focusMult, float socialMult, float duration)
    {
        if (duration <= 0f)
            return;

        energyDecayMult = Mathf.Max(0f, energyMult);
        focusDecayMult = Mathf.Max(0f, focusMult);
        socialDecayMult = Mathf.Max(0f, socialMult);
        decayOverrideUntil = Time.time + duration;
    }

    public void StartDance(float duration)
    {
        presentation.StartDance(duration);
    }

    public void ApplyHat(Sprite hatSprite, Vector3 localOffset, Vector3 sittingLocalOffset, Vector3 localScale)
    {
        presentation.ApplyHat(hatSprite, localOffset, sittingLocalOffset, localScale);
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
