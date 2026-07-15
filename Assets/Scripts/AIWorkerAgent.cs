using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(OfficeWorkerMotor2D))]
public class AIWorkerAgent : MonoBehaviour
{
    private enum WorkerState
    {
        Thinking,
        Planning,
        Moving,
        Acting,
        Replanning
    }

    [Header("Identity")]
    [Tooltip("Matches a HatPool.agentType in the HatCatalogSO. Agents with no matching pool are skipped by hat events.")]
    public string agentType = "";

    [Tooltip("Stable id matching an LLM agent profile. Falls back to the GameObject name.")]
    public string agentId = "";
    [Tooltip("Display name shown to the LLM. Falls back to agentId.")]
    public string displayName = "";
    [TextArea]
    [Tooltip("Free-text persona fed to the LLM (traits, job, quirks). Leave empty to use the LLMBrainService inspector entry with the same agentId.")]
    public string personality = "";

    [Header("References")]
    public OfficeGrid2D grid;

    [Header("Personal Space")]
    [Tooltip("Drag the specific desk this agent owns into this slot")]
    public OfficeActionPoint assignedDesk;

    [Header("Movement & Avoidance")]
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

    [Header("Local Avoidance")]
    [Tooltip("Body radius used for path clearance, worker spacing, and obstacle checks.")]
    public float avoidanceRadius = 0.3f;
    [Tooltip("Distance from the action slot where the worker can start acting.")]
    public float slotArriveDistance = 0.16f;
    [Tooltip("Near-target distance where a blocked worker stops chasing exact pixels and commits to the action.")]
    public float slotCommitRadius = 0.32f;
    [Tooltip("Max seconds a worker may hover near a target before starting the reserved action.")]
    public float slotCommitGrace = 0.45f;

    [Header("Right Of Way")]
    [Tooltip("Higher priority workers are less likely to yield in narrow spaces.")]
    public float urgencyPriorityWeight = 0.35f;

    [Header("Replanning")]
    [Tooltip("Re-plan if a moving worker makes no real progress for this many seconds.")]
    public float stuckTimeout = 2.25f;
    [Tooltip("Net displacement that counts as progress and resets the stuck timer.")]
    public float stuckMoveThreshold = 0.08f;
    [Tooltip("Minimum time between crowd-triggered replans.")]
    public float replanCooldown = 0.8f;
    [Tooltip("Number of upcoming path points reserved so other workers can route around this worker.")]
    public int reservedLookaheadPoints = 5;

    [Header("LLM Brain (optional)")]
    [Tooltip("If on, the agent periodically asks the LLM what to do, guided by its personality. Falls back to utility scoring when the LLM is unavailable or slow.")]
    public bool useLLMBrain = false;
    [Tooltip("Minimum seconds between LLM queries for this agent.")]
    public float brainDecisionInterval = 20f;
    [Tooltip("How long an LLM-chosen directive stays valid before reverting to utility scoring.")]
    public float brainDirectiveTtl = 30f;
    [Tooltip("Log each LLM decision and reason to the console for tuning.")]
    [SerializeField] private bool logBrainDecisions = false;
    [Tooltip("Show floating speech bubbles above this agent.")]
    public bool showThoughtBubble = true;
    [Tooltip("Show private LLM decision reasons for non-social actions. Keep off if you only want spoken dialogue.")]
    public bool showDecisionThoughtBubbles = false;
    [Tooltip("Optional: assign a custom thought bubble. Auto-created if left empty.")]
    [SerializeField] private AgentThoughtBubble thoughtBubble;

    private Rigidbody2D rb;
    private OfficeWorkerMotor2D motor;
    private OfficeCrowdCoordinator2D crowd;
    private OfficeActionPoint[] actionPoints;

    private WorkerState state = WorkerState.Thinking;
    private float stateTimer;
    private float stuckTimer;
    private float nearSlotTimer;
    private float nextReplanTime;
    private Vector2 stuckCheckPos;

    private string brainDirectiveActionId;
    private string brainDirectiveTargetAgent;
    private string brainDirectiveReason;
    private string pendingBrainThought;
    private float nextSocialCheckTime;
    private bool inConversation;
    private bool conversationStarting;
    private float brainDirectiveExpiry = -1f;
    private float nextBrainQueryTime;
    private bool brainQueryInFlight;

    private OfficeActionPoint currentAction;
    private List<Vector2> currentPath;
    private int pathIndex;
    private Vector2 currentSafeActionTarget;
    private readonly List<Vector2> actionApproachWaypoints = new List<Vector2>();
    private OfficeActionPoint departureAction;
    private float departureActionExpiresAt;
    private OfficeActionPoint lastFinishedDesk;
    private string lastActionLabel;
    private readonly Dictionary<string, int> affinity = new Dictionary<string, int>();
    private bool stillSeated;

    [SerializeField] private Animator anim;
    [SerializeField] private Transform hatAnchor;
    [SerializeField] private Vector3 defaultHatAnchorLocalPosition = new Vector3(0f, 0.5f, 0f);
    private Vector2 lastFacing = Vector2.down;
    private GameObject currentHat;
    private Transform runtimeHatAnchor;
    private Vector3 currentHatStandingOffset;
    private Vector3 currentHatSittingOffset;
    private bool lastHatSittingState;

    private float speedBuffMultiplier = 1f;
    private float speedBuffUntil = -1f;
    private float energyDecayMult = 1f;
    private float focusDecayMult = 1f;
    private float socialDecayMult = 1f;
    private float decayOverrideUntil = -1f;
    private Coroutine danceRoutine;

    public OfficeGrid2D Grid => grid;
    public string AgentId => !string.IsNullOrEmpty(agentId) ? agentId : name;
    public string DisplayName => !string.IsNullOrEmpty(displayName) ? displayName : AgentId;
    public Vector2 CurrentVelocity => motor != null ? motor.Velocity : Vector2.zero;
    public bool IsBlocking => state == WorkerState.Acting;
    public float CrowdSeparationWeight
    {
        get
        {
            switch (state)
            {
                case WorkerState.Moving:
                case WorkerState.Planning:
                case WorkerState.Replanning:
                    return 1f;
                case WorkerState.Thinking:
                    return 0.65f;
                case WorkerState.Acting:
                    return 0.2f;
                default:
                    return 0.5f;
            }
        }
    }

    private void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        motor = GetComponent<OfficeWorkerMotor2D>();
        if (motor == null)
            motor = gameObject.AddComponent<OfficeWorkerMotor2D>();

        if (grid == null)
            grid = FindFirstObjectByType<OfficeGrid2D>();

        if (anim == null)
            anim = GetComponentInChildren<Animator>();

        EnsureAgentType();

        crowd = OfficeCrowdCoordinator2D.Ensure();
        crowd.Register(this);

        RegisterBrainProfile();

        RefreshActionPoints();

        state = WorkerState.Thinking;
        stateTimer = Random.Range(0.2f, 1f);
        stuckCheckPos = rb.position;
    }

    private void Update()
    {
        TickNeeds(Time.deltaTime);

        switch (state)
        {
            case WorkerState.Thinking:
                motor.Stop();
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f)
                    DecideNextAction();
                break;

            case WorkerState.Planning:
                PlanCurrentAction();
                break;

            case WorkerState.Moving:
                TickMoving();
                break;

            case WorkerState.Replanning:
                PlanCurrentAction();
                break;

            case WorkerState.Acting:
                motor.Stop();
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f)
                {
                    FinishAction();
                }
                else if (currentAction != null && IsSocialSpot(currentAction.actionType)
                         && Time.time >= nextSocialCheckTime)
                {
                    nextSocialCheckTime = Time.time + 0.7f;
                    TryStartSocialExchange();
                }
                break;
        }
        UpdateAnimation();
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
    public bool TryApplyCrowdSeparation(Vector2 displacement, Vector2 otherPosition)
    {
        if (displacement.sqrMagnitude <= 0.000001f)
            return false;

        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        if (grid == null || rb == null)
            return false;

        Vector2 from = GetPosition();
        Vector2 to = from + displacement;
        if (Vector2.Distance(to, otherPosition) <= Vector2.Distance(from, otherPosition) + 0.0001f)
            return false;

        if (!grid.CanRecoverBody(from, to, avoidanceRadius))
            return false;

        rb.MovePosition(to);
        if (motor != null)
            motor.Stop();

        stuckCheckPos = to;
        stuckTimer = 0f;
        nextReplanTime = Mathf.Min(nextReplanTime, Time.time + replanCooldown * 0.5f);
        return true;
    }

    // Kept so older debug/experimental components still compile while the
    // movement authority moves to OfficeWorkerMotor2D.
    public void ApplyRVOVelocity(Vector2 velocity, float dt)
    {
        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        rb.MovePosition(rb.position + velocity * dt);
    }

    public bool TryGetProtectedInteractionSpace(out Vector2 furniturePoint, out Vector2 workerPoint, out float radius)
    {
        furniturePoint = GetPosition();
        workerPoint = GetPosition();
        radius = 0f;

        if (state != WorkerState.Acting || currentAction == null)
            return false;

        return currentAction.TryGetInteractionBlocker(
            grid,
            GetPosition(),
            avoidanceRadius,
            out furniturePoint,
            out workerPoint,
            out radius);
    }

    public bool TryGetProtectedActionGroup(AIWorkerAgent requester, out Vector2 center, out float radius)
    {
        center = GetPosition();
        radius = 0f;

        if (currentAction == null)
            return false;

        if (requester != null && currentAction.IsReservedBy(requester))
            return false;

        return currentAction.TryGetFullActionBlocker(avoidanceRadius, out center, out radius);
    }

    public float GetRightOfWayPriority()
    {
        float urgency = (100f - energy) + (100f - focus) + (100f - social);
        float actionBonus = currentAction != null && currentAction.actionType == OfficeActionType.WorkDesk ? 10f : 0f;
        float stableTieBreaker = Mathf.Abs(GetInstanceID() % 1000) * 0.001f;
        return urgency * urgencyPriorityWeight + actionBonus + stableTieBreaker;
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

    public void RefreshActionPoints()
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
        state = WorkerState.Planning;
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
                AIWorkerAgent named = FindWorkerByDisplayName(brainDirectiveTargetAgent);
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

        AgentStateSnapshot snapshot = new AgentStateSnapshot
        {
            energy = energy,
            focus = focus,
            social = social,
            productivity = productivity,
            mood = DeriveMood(),
            lastAction = lastActionLabel ?? "",
            relationships = BuildRelationships(),
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
            personality = personality ?? ""
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

    private void IncrementAffinity(string name)
    {
        if (string.IsNullOrEmpty(name))
            return;

        if (affinity.ContainsKey(name))
            affinity[name]++;
        else
            affinity[name] = 1;
    }

    private string BuildRelationships()
    {
        if (affinity.Count == 0)
            return "";

        string result = "";
        int count = 0;
        foreach (KeyValuePair<string, int> kvp in affinity)
        {
            if (kvp.Value <= 0)
                continue;

            if (count >= 4)
                break;

            result += (result.Length > 0 ? ", " : "") + kvp.Key + "(" + kvp.Value + ")";
            count++;
        }

        return result;
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
        List<ActionOption> options = new List<ActionOption>();
        HashSet<OfficeActionType> seen = new HashSet<OfficeActionType>();

        if (actionPoints != null)
        {
            foreach (OfficeActionPoint actionPoint in actionPoints)
            {
                if (actionPoint == null || !seen.Add(actionPoint.actionType))
                    continue;

                options.Add(new ActionOption
                {
                    actionId = actionPoint.actionType.ToString(),
                    label = ActionLabel(actionPoint.actionType)
                });
            }
        }

        return options;
    }

    private static string ActionLabel(OfficeActionType type)
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

    private static string ActionDisplayLabel(OfficeActionType type)
    {
        switch (type)
        {
            case OfficeActionType.WorkDesk: return "work at the desk";
            case OfficeActionType.CoffeeMachine: return "grab coffee";
            case OfficeActionType.BreakSpot: return "take a break";
            case OfficeActionType.ChatSpot: return "chat with coworkers";
            case OfficeActionType.MeetingRoom: return "join a meeting";
            default: return type.ToString();
        }
    }

    private void EnsureThoughtBubble()
    {
        if (thoughtBubble != null)
            return;

        GameObject go = new GameObject("ThoughtBubble");
        go.transform.SetParent(transform, false);
        thoughtBubble = go.AddComponent<AgentThoughtBubble>();
    }

    private float EstimateRoutePenalty(OfficeActionPoint actionPoint)
    {
        if (grid == null || actionPoint == null)
            return 0f;

        Vector2 target = actionPoint.GetTargetPosition(this);
        float distancePenalty = Vector2.Distance(GetPosition(), target) * 0.35f;
        Vector2Int targetCell = grid.WorldToCell(target);
        float crowdPenalty = crowd != null ? crowd.GetPathCost(targetCell, this) : 0f;
        return distancePenalty + crowdPenalty;
    }

    private void PlanCurrentAction()
    {
        if (currentAction == null)
        {
            AbortMovement();
            return;
        }

        Vector2 targetPosition = currentAction.GetTargetPosition(this);
        currentPath = FindPathToCurrentAction(targetPosition);
        if (currentPath == null || currentPath.Count == 0)
        {
            currentAction.Release(this);
            currentAction = null;
            state = WorkerState.Thinking;
            stateTimer = decisionDelay;
            return;
        }

        currentSafeActionTarget = currentPath[currentPath.Count - 1];
        pathIndex = Mathf.Min(1, currentPath.Count - 1);
        stuckTimer = 0f;
        nearSlotTimer = 0f;
        stuckCheckPos = GetPosition();
        nextReplanTime = Time.time + replanCooldown;
        state = WorkerState.Moving;
    }

    private List<Vector2> FindPathToCurrentAction(Vector2 targetPosition)
    {
        List<Vector2> fullPath = null;
        Vector2 segmentStart = GetPosition();

        if (!AddDepartureWaypoint(targetPosition, segmentStart, ref fullPath, ref segmentStart))
            return null;

        if (!currentAction.GetApproachWaypoints(this, grid, avoidanceRadius, actionApproachWaypoints))
            return null;

        for (int i = 0; i < actionApproachWaypoints.Count; i++)
        {
            Vector2 waypoint = actionApproachWaypoints[i];
            if (Vector2.Distance(segmentStart, waypoint) <= arriveDistance)
                continue;

            List<Vector2> segment = OfficePathfinder2D.FindPath(grid, segmentStart, waypoint, this, avoidanceRadius);
            if (!AppendPathSegment(ref fullPath, segment))
                return null;

            segmentStart = fullPath[fullPath.Count - 1];
        }

        List<Vector2> finalSegment = OfficePathfinder2D.FindPath(grid, segmentStart, targetPosition, this, avoidanceRadius);
        if (!AppendPathSegment(ref fullPath, finalSegment))
            return null;

        return fullPath;
    }

    private bool AddDepartureWaypoint(
        Vector2 targetPosition,
        Vector2 initialSegmentStart,
        ref List<Vector2> fullPath,
        ref Vector2 segmentStart)
    {
        if (departureAction == null || departureAction == currentAction || Time.time > departureActionExpiresAt)
        {
            departureAction = null;
            return true;
        }

        bool requiresDeparture = departureAction.NeedsDepartureWaypoint(initialSegmentStart, targetPosition, avoidanceRadius);
        if (!requiresDeparture)
        {
            departureAction = null;
            return true;
        }

        if (!departureAction.TryGetDepartureWaypoint(
                initialSegmentStart,
                targetPosition,
                grid,
                avoidanceRadius,
                out Vector2 waypoint))
            return false;

        if (Vector2.Distance(segmentStart, waypoint) <= arriveDistance)
        {
            departureAction = null;
            return true;
        }

        List<Vector2> segment = OfficePathfinder2D.FindPath(grid, segmentStart, waypoint, this, avoidanceRadius);
        if (!AppendPathSegment(ref fullPath, segment))
            return false;

        departureAction = null;
        segmentStart = fullPath[fullPath.Count - 1];
        return true;
    }

    private static bool AppendPathSegment(ref List<Vector2> fullPath, List<Vector2> segment)
    {
        if (segment == null || segment.Count == 0)
            return false;

        if (fullPath == null)
        {
            fullPath = new List<Vector2>(segment);
            return true;
        }

        int startIndex = Vector2.Distance(fullPath[fullPath.Count - 1], segment[0]) <= 0.001f ? 1 : 0;
        for (int i = startIndex; i < segment.Count; i++)
            fullPath.Add(segment[i]);

        return true;
    }

    private void TickMoving()
    {
        if (currentPath == null || currentPath.Count == 0 || currentAction == null)
        {
            AbortMovement();
            return;
        }

        AdvancePathIndex();
        if (pathIndex >= currentPath.Count)
        {
            StartActing();
            return;
        }

        crowd.ReserveUpcomingPath(this, grid, currentPath, pathIndex, reservedLookaheadPoints);

        Vector2 target = CurrentPathTarget();
        Vector2 pos = GetPosition();
        float dist = Vector2.Distance(pos, target);
        bool isLast = pathIndex == currentPath.Count - 1;

        if (isLast && dist < slotCommitRadius)
        {
            nearSlotTimer += Time.deltaTime;
            if (nearSlotTimer >= slotCommitGrace)
            {
                StartActing();
                return;
            }
        }
        else
        {
            nearSlotTimer = 0f;
        }

        bool moved = motor.MoveToward(this, grid, crowd, target, avoidanceRadius, speed * EffectiveSpeedMultiplier, Time.deltaTime);
        TrackStuck(moved);
    }

    private Vector2 CurrentPathTarget()
    {
        if (pathIndex == currentPath.Count - 1 && currentAction != null)
            return currentSafeActionTarget;

        return currentPath[pathIndex];
    }

    private void AdvancePathIndex()
    {
        while (pathIndex < currentPath.Count)
        {
            Vector2 target = CurrentPathTarget();
            bool isLast = pathIndex == currentPath.Count - 1;
            float threshold = isLast ? slotArriveDistance : arriveDistance;
            if (Vector2.Distance(GetPosition(), target) > threshold)
                break;

            pathIndex++;
        }
    }

    private void TrackStuck(bool moved)
    {
        if (Vector2.Distance(GetPosition(), stuckCheckPos) > stuckMoveThreshold)
        {
            stuckCheckPos = GetPosition();
            stuckTimer = 0f;
            return;
        }

        stuckTimer += Time.deltaTime;

        if ((!moved || stuckTimer >= stuckTimeout) && Time.time >= nextReplanTime)
        {
            state = WorkerState.Replanning;
            nextReplanTime = Time.time + replanCooldown;
        }
    }

    private void StartActing()
    {
        motor.Stop();
        currentPath = null;
        pathIndex = 0;
        state = WorkerState.Acting;
        stateTimer = currentAction != null ? currentAction.useTime : 1f;

        // Waiting for someone to chat with: linger longer so they can arrive.
        if (currentAction != null && IsSocialSpot(currentAction.actionType)
            && !string.IsNullOrEmpty(brainDirectiveReason))
        {
            stateTimer = Mathf.Max(stateTimer, 20f);
        }

        if (currentAction != null)
            lastFacing = currentAction.GetFacingVector(this);

        if (showThoughtBubble && showDecisionThoughtBubbles && !string.IsNullOrEmpty(pendingBrainThought)
            && currentAction != null && !IsSocialSpot(currentAction.actionType))
        {
            EnsureThoughtBubble();
            if (thoughtBubble != null)
            {
                string label = ActionDisplayLabel(currentAction.actionType);
                thoughtBubble.Show($"<b>{DisplayName}</b> <size=22><color=#9aa9b6>· {label}</color></size>\n<i>\"{pendingBrainThought}\"</i>");
            }
        }
        pendingBrainThought = null;

        nextSocialCheckTime = Time.time + 0.5f;
        TryStartSocialExchange();
    }

    private void TryStartSocialExchange()
    {
        if (inConversation || conversationStarting)
            return;

        if (currentAction == null || !IsSocialSpot(currentAction.actionType))
            return;

        List<AIWorkerAgent> participants = FindNearbyParticipants(1.5f);
        if (participants.Count == 0)
            return; // nobody to talk to; retry while lingering

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || !brain.EnableSocialReplies)
            return;

        // Only one agent present should start: the lowest-id LLM agent.
        if (!IsConversationStarter(participants))
            return;

        string line = brainDirectiveReason;
        if (!string.IsNullOrEmpty(line))
            BeginConversation(participants, line);
        else
            BeginGeneratedConversation(participants);
    }

    private bool IsConversationStarter(List<AIWorkerAgent> participants)
    {
        if (!useLLMBrain)
            return false;

        int myId = GetInstanceID();
        foreach (AIWorkerAgent p in participants)
        {
            if (p != null && p.useLLMBrain && p.GetInstanceID() < myId)
                return false;
        }

        return true;
    }

    private void BeginConversation(List<AIWorkerAgent> participants, string line)
    {
        brainDirectiveTargetAgent = "";
        brainDirectiveReason = "";
        brainDirectiveActionId = "";

        inConversation = true;
        foreach (AIWorkerAgent p in participants)
        {
            if (p == null)
                continue;
            p.inConversation = true;
            p.brainDirectiveTargetAgent = "";
            p.brainDirectiveReason = "";
            p.brainDirectiveActionId = "";
        }

        ExtendActing(14f);
        foreach (AIWorkerAgent p in participants)
            if (p != null)
                p.ExtendActing(14f);

        if (showThoughtBubble)
        {
            EnsureThoughtBubble();
            if (thoughtBubble != null)
                thoughtBubble.Show($"<b>{DisplayName}</b> <size=22><color=#9aa9b6>· says</color></size>\n<i>\"{line}\"</i>");
        }

        ApplyEffects(0f, 0f, 6f, 0f);

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
        {
            EndConversation(participants);
            return;
        }

        foreach (AIWorkerAgent p in participants)
            if (p != null)
                brain.Remember(p.AgentId, DisplayName + " said: " + line);

        RunConversation(participants, line);
    }

    private void BeginGeneratedConversation(List<AIWorkerAgent> participants)
    {
        conversationStarting = true;
        inConversation = true;
        foreach (AIWorkerAgent p in participants)
            if (p != null)
                p.inConversation = true;

        ExtendActing(8f);
        foreach (AIWorkerAgent p in participants)
            if (p != null)
                p.ExtendActing(8f);

        try
        {
            string opener = BuildFallbackOpener(participants);

            if (string.IsNullOrEmpty(opener) || currentAction == null || !IsSocialSpot(currentAction.actionType))
            {
                nextSocialCheckTime = Time.time + 8f; // cooldown before retrying
                EndConversation(participants);
                return;
            }

            BeginConversation(participants, opener);
        }
        catch
        {
            nextSocialCheckTime = Time.time + 8f;
            EndConversation(participants);
        }
        finally
        {
            conversationStarting = false;
        }
    }

    private string BuildFallbackOpener(List<AIWorkerAgent> participants)
    {
        if (participants != null && participants.Count > 0 && participants[0] != null)
            return "Hey " + participants[0].DisplayName + ", how's your day going?";

        return "Anyone want to chat for a minute?";
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

    private static bool IsSocialSpot(OfficeActionType type)
    {
        return type == OfficeActionType.ChatSpot || type == OfficeActionType.BreakSpot;
    }

    private List<AIWorkerAgent> FindNearbyParticipants(float radius)
    {
        List<AIWorkerAgent> result = new List<AIWorkerAgent>();
        if (crowd == null)
            return result;

        for (int i = 0; i < crowd.Workers.Count; i++)
        {
            AIWorkerAgent worker = crowd.Workers[i];
            if (worker == null || worker == this || worker.inConversation)
                continue;

            if (Vector2.Distance(GetPosition(), worker.GetPosition()) <= radius)
                result.Add(worker);
        }

        return result;
    }

    private async void RunConversation(List<AIWorkerAgent> participants, string openerLine)
    {
        LLMBrainService brain = LLMBrainService.Instance;

        List<AIWorkerAgent> speakers = new List<AIWorkerAgent> { this };
        foreach (AIWorkerAgent p in participants)
            if (p != null && p != this)
                speakers.Add(p);

        string names = BuildParticipantNames(speakers);
        string lastSpeaker = DisplayName;
        string lastLine = openerLine;

        const int maxTurns = 6;

        try
        {
            for (int turn = 1; turn < maxTurns; turn++)
            {
                AIWorkerAgent speaker = speakers[turn % speakers.Count];
                if (speaker == null || NeedsToLeave(speaker))
                    break;

                string speakerId = speaker == this ? AgentId : speaker.AgentId;
                string line = brain != null
                    ? await brain.ConverseAsync(speakerId, names, lastSpeaker, lastLine)
                    : null;

                if (string.IsNullOrEmpty(line))
                {
                    SetConversationCooldown(speakers, 8f);
                    line = IsQuestionLine(lastLine)
                        ? BuildFallbackQuestionReply(lastSpeaker)
                        : BuildFallbackConversationReply(lastSpeaker);
                }

                if (speaker == null || string.IsNullOrEmpty(line))
                    break;

                speaker.ShowThought($"<b>{speaker.DisplayName}</b> <size=22><color=#9aa9b6>· says</color></size>\n<i>\"{line}\"</i>");
                speaker.ApplyEffects(0f, 0f, 5f, 0f);
                speaker.IncrementAffinity(lastSpeaker);

                foreach (AIWorkerAgent s in speakers)
                    if (s != null)
                        s.ExtendActing(12f);

                if (brain != null)
                    brain.Remember(speakerId, lastSpeaker + ": " + lastLine);

                lastSpeaker = speaker.DisplayName;
                lastLine = line;

            }
        }
        catch
        {
            // A failed turn is non-critical.
        }
        finally
        {
            EndConversation(participants);
        }
    }

    private static bool IsQuestionLine(string line)
    {
        return !string.IsNullOrEmpty(line) && line.IndexOf('?') >= 0;
    }

    private static string BuildFallbackQuestionReply(string lastSpeaker)
    {
        if (string.IsNullOrEmpty(lastSpeaker))
            return "I'm doing alright, thanks.";

        return "I'm doing alright, thanks for asking, " + lastSpeaker + ".";
    }

    private static string BuildFallbackConversationReply(string lastSpeaker)
    {
        if (string.IsNullOrEmpty(lastSpeaker))
            return "Sorry, I got distracted for a second.";

        return "Sorry, " + lastSpeaker + ", I got distracted for a second.";
    }

    private static void SetConversationCooldown(List<AIWorkerAgent> speakers, float seconds)
    {
        if (speakers == null)
            return;

        float until = Time.time + seconds;
        foreach (AIWorkerAgent speaker in speakers)
        {
            if (speaker != null)
                speaker.nextSocialCheckTime = Mathf.Max(speaker.nextSocialCheckTime, until);
        }
    }

    private void EndConversation(List<AIWorkerAgent> participants)
    {
        inConversation = false;
        if (participants == null)
            return;

        foreach (AIWorkerAgent p in participants)
            if (p != null)
                p.inConversation = false;
    }

    private static bool NeedsToLeave(AIWorkerAgent agent)
    {
        if (agent == null)
            return true;

        return agent.energy < 30f || agent.focus < 30f;
    }

    private static string BuildParticipantNames(List<AIWorkerAgent> speakers)
    {
        string result = "";
        foreach (AIWorkerAgent s in speakers)
        {
            if (s == null)
                continue;

            if (result.Length > 0)
                result += ", ";
            result += s.DisplayName;
        }

        return result;
    }

    private AIWorkerAgent FindWorkerByDisplayName(string displayName)
    {
        if (crowd == null || string.IsNullOrEmpty(displayName))
            return null;

        for (int i = 0; i < crowd.Workers.Count; i++)
        {
            AIWorkerAgent worker = crowd.Workers[i];
            if (worker != null && worker != this
                && string.Equals(worker.DisplayName, displayName, System.StringComparison.OrdinalIgnoreCase))
            {
                return worker;
            }
        }

        return null;
    }

    public void ShowThought(string content)
    {
        if (!showThoughtBubble)
            return;

        EnsureThoughtBubble();
        if (thoughtBubble != null)
            thoughtBubble.Show(content);
    }

    private void FinishAction()
    {
        if (currentAction != null)
        {
            OfficeActionPoint finishedAction = currentAction;
            float departureHoldSeconds = Mathf.Max(1f, decisionDelay + replanCooldown + 0.5f);
            currentAction.ApplyTo(this);
            currentAction.Release(this);
            currentAction.HoldDepartingAgent(this, departureHoldSeconds);
            departureAction = finishedAction;
            departureActionExpiresAt = Time.time + departureHoldSeconds;

            if (finishedAction.actionType == OfficeActionType.WorkDesk)
            {
                lastFinishedDesk = finishedAction;
                stillSeated = true;
            }

            lastActionLabel = ActionDisplayLabel(finishedAction.actionType);

            PhysicalVirtualInteractionBridge bridge = PhysicalVirtualInteractionBridge.Instance;
            if (bridge != null)
                bridge.EvaluateProductivityMilestone();

            currentAction = null;
        }

        currentPath = null;
        pathIndex = 0;
        nearSlotTimer = 0f;
        stuckTimer = 0f;
        motor.Stop();

        state = WorkerState.Thinking;
        stateTimer = decisionDelay;
    }

    private void AbortMovement()
    {
        if (currentAction != null)
        {
            currentAction.Release(this);
            currentAction = null;
        }

        departureAction = null;
        pendingBrainThought = null;

        currentPath = null;
        pathIndex = 0;
        nearSlotTimer = 0f;
        stuckTimer = 0f;
        motor.Stop();

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

    public void ApplyCosmeticTint(Color tint)
    {
        SpriteRenderer[] renderers = GetComponentsInChildren<SpriteRenderer>();
        foreach (SpriteRenderer renderer in renderers)
            renderer.color = tint;
    }

    public void StartDance(float duration)
    {
        if (duration <= 0f)
            return;

        if (danceRoutine != null)
            StopCoroutine(danceRoutine);

        danceRoutine = StartCoroutine(DanceRoutine(duration));
    }

    private IEnumerator DanceRoutine(float duration)
    {
        Transform visualRoot = anim != null ? anim.transform : transform;
        Vector3 baseLocalPosition = visualRoot.localPosition;
        Vector3 baseLocalScale = visualRoot.localScale;

        float elapsed = 0f;
        float phase = Random.Range(0f, Mathf.PI * 2f);
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float beat = Mathf.Sin((elapsed * 9f) + phase);
            float side = Mathf.Sin((elapsed * 5.5f) + phase);
            visualRoot.localPosition = baseLocalPosition + new Vector3(side * 0.045f, Mathf.Abs(beat) * 0.09f, 0f);
            visualRoot.localScale = baseLocalScale * (1f + Mathf.Abs(beat) * 0.08f);

            yield return null;
        }

        visualRoot.localPosition = baseLocalPosition;
        visualRoot.localScale = baseLocalScale;

        danceRoutine = null;
    }

    private void EnsureAgentType()
    {
        if (!string.IsNullOrEmpty(agentType))
            return;

        Transform visualRoot = anim != null ? anim.transform : transform;
        SpriteRenderer renderer = visualRoot.GetComponentInChildren<SpriteRenderer>();
        if (renderer == null || renderer.sprite == null)
            return;

        string spriteName = renderer.sprite.name;
        int separator = spriteName.IndexOf('_');
        agentType = separator > 0 ? spriteName.Substring(0, separator) : spriteName;
    }

    public void ApplyHat(Sprite hatSprite, Vector3 localOffset, Vector3 sittingLocalOffset, Vector3 localScale)
    {
        if (hatSprite == null)
            return;

        Transform visualRoot = anim != null ? anim.transform : transform;
        Transform host = ResolveHatHost(visualRoot);

        if (currentHat != null)
            Destroy(currentHat);

        currentHatStandingOffset = localOffset;
        currentHatSittingOffset = sittingLocalOffset;
        lastHatSittingState = IsCurrentlySitting();

        SpriteRenderer bodyRenderer = visualRoot.GetComponentInChildren<SpriteRenderer>();

        currentHat = new GameObject("Hat");
        currentHat.transform.SetParent(host, false);
        currentHat.transform.localPosition = CurrentHatOffset();
        currentHat.transform.localScale = localScale;

        SpriteRenderer sr = currentHat.AddComponent<SpriteRenderer>();
        sr.sprite = hatSprite;
        sr.color = Color.white;
        if (bodyRenderer != null)
        {
            sr.sortingLayerID = bodyRenderer.sortingLayerID;
            sr.sortingOrder = bodyRenderer.sortingOrder + 20;
        }
        else
        {
            sr.sortingOrder = 100;
        }
    }

    private Vector3 CurrentHatOffset()
    {
        return IsCurrentlySitting() ? currentHatSittingOffset : currentHatStandingOffset;
    }

    private void UpdateHatPlacement(bool isSitting)
    {
        if (currentHat == null && isSitting == lastHatSittingState)
            return;

        lastHatSittingState = isSitting;

        if (currentHat != null)
            currentHat.transform.localPosition = isSitting ? currentHatSittingOffset : currentHatStandingOffset;
    }

    private bool IsCurrentlySitting()
    {
        return stillSeated || (state == WorkerState.Acting && currentAction != null && currentAction.actionType == OfficeActionType.WorkDesk);
    }

    private Transform ResolveHatHost(Transform visualRoot)
    {
        if (hatAnchor != null)
            return hatAnchor;

        Transform found = FindChildRecursive(visualRoot, "HatAnchor");
        if (found != null)
            return found;

        if (runtimeHatAnchor == null)
        {
            GameObject anchorObject = new GameObject("HatAnchor");
            runtimeHatAnchor = anchorObject.transform;
            runtimeHatAnchor.SetParent(visualRoot, false);
            runtimeHatAnchor.localPosition = defaultHatAnchorLocalPosition;
        }

        return runtimeHatAnchor;
    }

    private static Transform FindChildRecursive(Transform root, string childName)
    {
        if (root == null)
            return null;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindChildRecursive(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void UpdateAnimation()
    {
        if (anim == null || motor == null)
            return;

        Vector2 velocity = motor.Velocity;
        bool isMoving = velocity.sqrMagnitude > 0.0001f;

        if (isMoving)
        {
            Vector2 dir = velocity.normalized;
            if (Mathf.Abs(dir.x) > Mathf.Abs(dir.y))
                lastFacing = new Vector2(Mathf.Sign(dir.x), 0f);
            else
                lastFacing = new Vector2(0f, Mathf.Sign(dir.y));
        }

        anim.SetBool("IsMoving", isMoving);
        anim.SetFloat("MoveX", lastFacing.x);
        anim.SetFloat("MoveY", lastFacing.y);

        if (stillSeated && lastFinishedDesk != null)
        {
            Vector2 deskTarget = lastFinishedDesk.GetTargetPosition(this);
            if (Vector2.Distance(GetPosition(), deskTarget) > slotCommitRadius)
                stillSeated = false;
        }

        bool isSitting = IsCurrentlySitting();
        anim.SetBool("IsSitting", isSitting);
        UpdateHatPlacement(isSitting);
    }
}
