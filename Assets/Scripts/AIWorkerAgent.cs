using System.Collections.Generic;
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

    private OfficeActionPoint currentAction;
    private List<Vector2> currentPath;
    private int pathIndex;
    private Vector2 currentSafeActionTarget;

    public OfficeGrid2D Grid => grid;
    public Vector2 CurrentVelocity => motor != null ? motor.Velocity : Vector2.zero;
    public bool IsBlocking => state == WorkerState.Acting;

    private void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        motor = GetComponent<OfficeWorkerMotor2D>();
        if (motor == null)
            motor = gameObject.AddComponent<OfficeWorkerMotor2D>();

        if (grid == null)
        {
#if UNITY_2023_1_OR_NEWER
            grid = FindFirstObjectByType<OfficeGrid2D>();
#else
            grid = FindObjectOfType<OfficeGrid2D>();
#endif
        }

        crowd = OfficeCrowdCoordinator2D.Ensure();
        crowd.Register(this);

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
                    FinishAction();
                break;
        }
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

    // Kept so older debug/experimental components still compile while the
    // movement authority moves to OfficeWorkerMotor2D.
    public void ApplyRVOVelocity(Vector2 velocity, float dt)
    {
        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        rb.MovePosition(rb.position + velocity * dt);
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
        energy = Mathf.Clamp(energy - energyDecay * deltaTime, 0f, 100f);
        focus = Mathf.Clamp(focus - focusDecay * deltaTime, 0f, 100f);
        social = Mathf.Clamp(social - socialDecay * deltaTime, 0f, 100f);
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
            float routePenalty = EstimateRoutePenalty(actionPoint);
            score -= routePenalty;

            if (score > bestScore)
            {
                bestScore = score;
                bestAction = actionPoint;
            }
        }

        if (bestAction == null || !bestAction.TryReserve(this))
        {
            stateTimer = decisionDelay;
            return;
        }

        currentAction = bestAction;
        state = WorkerState.Planning;
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
        currentPath = OfficePathfinder2D.FindPath(grid, GetPosition(), targetPosition, this, avoidanceRadius);
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

        bool moved = motor.MoveToward(this, grid, crowd, target, avoidanceRadius, speed, Time.deltaTime);
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
    }

    private void FinishAction()
    {
        if (currentAction != null)
        {
            currentAction.ApplyTo(this);
            currentAction.Release(this);
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
    }
}
