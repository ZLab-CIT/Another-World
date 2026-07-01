using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class AIWorkerAgent : MonoBehaviour
{
    private enum WorkerState
    {
        Thinking,
        Moving,
        Acting
    }

    [Header("References")]
    public OfficeGrid2D grid;

    [Header("Personal Space")]
    [Tooltip("Drag the specific desk this agent owns into this slot")]
    public OfficeActionPoint assignedDesk;

    [Header("Movement & Avoidance")]
    public float speed = 2f;
    public float arriveDistance = 0.05f;

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

    [Header("Local Avoidance (RVO)")]
    [Tooltip("Personal-space radius used by RVO. Keep below half the grid cell size so agents use smooth cone avoidance instead of the hard collision push.")]
    public float avoidanceRadius = 0.22f;
    [Tooltip("Distance from the reserved slot at which the agent commits to acting instead of chasing exact position.")]
    public float slotArriveDistance = 0.15f;
    [Tooltip("When the agent is within this distance of its slot but cannot close the final gap (e.g. a neighbor is in the way), it starts a grace timer instead of orbiting the slot and shaking.")]
    public float slotCommitRadius = 0.3f;
    [Tooltip("Max seconds the agent may hover within slotCommitRadius without reaching slotArriveDistance before it commits to acting.")]
    public float slotCommitGrace = 0.4f;

    [Header("Right Of Way")]
    [Tooltip("Two agents meeting head-on within this distance resolve the deadlock by the lower-priority one stopping (becoming a static obstacle) so the other can swerve past.")]
    public float yieldDistance = 1.5f;

    [Header("Stuck Recovery")]
    [Tooltip("Abort the current move and re-plan if the agent makes no net progress for this many seconds.")]
    public float stuckTimeout = 4f;
    [Tooltip("Net displacement (m) that counts as progress and resets the stuck timer.")]
    public float stuckMoveThreshold = 0.1f;

    private float stuckTimer;
    private Vector2 stuckCheckPos;
    private float nearSlotTimer;

    private Rigidbody2D rb;
    private RVOAgent rvoAgent;
    private OfficeActionPoint[] actionPoints;

    private WorkerState state = WorkerState.Thinking;
    private float stateTimer;

    private OfficeActionPoint currentAction;
    private List<Vector2> currentPath;
    private int pathIndex;

    private void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        rb.gravityScale = 0f;
        rb.freezeRotation = true;

        if (grid == null)
        {
#if UNITY_2023_1_OR_NEWER
            grid = FindFirstObjectByType<OfficeGrid2D>();
#else
            grid = FindObjectOfType<OfficeGrid2D>();
#endif
        }

        RefreshActionPoints();

        rvoAgent = RVOSimulator.Ensure().Register(this, avoidanceRadius);
        rvoAgent.maxSpeed = speed;
        // Keep the RVO agent active permanently so it always participates in collision avoidance
        rvoAgent.active = true; 

        state = WorkerState.Thinking;
        stateTimer = Random.Range(0.2f, 1f);
    }

    private void Update()
    {
        TickNeeds(Time.deltaTime);

        if (state == WorkerState.Thinking)
        {
            rvoAgent.prefVelocity = Vector2.zero; // Stand still while thinking
            stateTimer -= Time.deltaTime;

            if (stateTimer <= 0f)
                DecideNextAction();
        }
        else if (state == WorkerState.Moving)
        {
            AdvancePath();

            if (state == WorkerState.Moving && rvoAgent != null)
            {
                rvoAgent.maxSpeed = speed;

                Vector2 target = CurrentPathTarget();
                Vector2 toTarget = target - rb.position;
                float dist = toTarget.magnitude;
                bool isLast = pathIndex == currentPath.Count - 1;
                float threshold = isLast ? slotArriveDistance : arriveDistance;

                if (isLast && currentAction != null && dist > threshold && dist < slotCommitRadius)
                {
                    nearSlotTimer += Time.deltaTime;
                    if (nearSlotTimer >= slotCommitGrace)
                    {
                        StartActing();
                    }
                }
                else
                {
                    nearSlotTimer = 0f;
                }

                if (state == WorkerState.Moving)
                {
                    if (dist <= threshold)
                    {
                        rvoAgent.prefVelocity = Vector2.zero;
                    }
                    else
                    {
                        float brakeRadius = isLast ? slotCommitRadius : arriveDistance * 2f;
                        float desiredSpeed = dist < brakeRadius
                            ? speed * Mathf.Clamp01(dist / brakeRadius)
                            : speed;

                        if (isLast && SlotApproachBlocked(toTarget, dist))
                            desiredSpeed = 0f;

                        rvoAgent.prefVelocity = (toTarget / dist) * desiredSpeed;
                    }
                }

                if (state == WorkerState.Moving && ShouldYield(toTarget, dist))
                {
                    rvoAgent.prefVelocity = Vector2.zero; // Yield by stopping, RVO will handle the rest
                    stuckTimer = 0f;
                    stuckCheckPos = rb.position;
                }
            }

            if (state == WorkerState.Moving)
            {
                if (Vector2.Distance(rb.position, stuckCheckPos) > stuckMoveThreshold)
                {
                    stuckCheckPos = rb.position;
                    stuckTimer = 0f;
                }
                else
                {
                    stuckTimer += Time.deltaTime;
                    if (stuckTimer >= stuckTimeout)
                        AbortMovement();
                }
            }
        }
        else if (state == WorkerState.Acting)
        {
            stateTimer -= Time.deltaTime;

            // When acting, preferred velocity is zero. 
            // We DO NOT force disable RVO. We let the agent become a stationary obstacle
            // that other RVO agents will gently flow around.
            if (rvoAgent != null)
            {
                rvoAgent.prefVelocity = Vector2.zero;
            }

            // CRITICAL FIX: Smoothly drift towards the exact slot position using RVO, 
            // NOT a forced Rigidbody snap. If someone leaves the group, they will naturally 
            // ease into the new position without violent shaking.
            if (currentAction != null)
            {
                Vector2 correctPos = currentAction.GetTargetPosition(this);
                Vector2 toCorrect = correctPos - rb.position;
                float distToCorrect = toCorrect.magnitude;
                
                if (distToCorrect > 0.05f)
                {
                    // Gentle nudge towards the correct position using the RVO system
                    rvoAgent.prefVelocity = (toCorrect / distToCorrect) * (speed * 0.25f);
                }
            }

            if (stateTimer <= 0f)
                FinishAction();
        }
    }

    // FixedUpdate is intentionally empty now. RVO Simulator handles the actual rb.MovePosition
    // via ApplyRVOVelocity. We removed the direct rb.MovePosition hack that was fighting the RVO.
    private void FixedUpdate()
    {
    }

    public Vector2 GetPosition()
    {
        return rb.position;
    }

    public bool IsBlocking => state == WorkerState.Acting;

    public void ApplyRVOVelocity(Vector2 velocity, float dt)
    {
        rb.MovePosition(rb.position + velocity * dt);
    }

    private void OnDestroy()
    {
        if (rvoAgent != null && RVOSimulator.Instance != null)
            RVOSimulator.Instance.Unregister(rvoAgent);
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

            // Personal Workspaces
            if (actionPoint.actionType == OfficeActionType.WorkDesk)
            {
                if (assignedDesk != null && actionPoint != assignedDesk)
                    continue;
            }

            if (actionPoint.IsReservedByOther(this))
                continue;

            float score = ScoreAction(actionPoint);

            if (score > bestScore)
            {
                bestScore = score;
                bestAction = actionPoint;
            }
        }

        if (bestAction == null)
        {
            stateTimer = decisionDelay;
            return;
        }

        if (!bestAction.TryReserve(this))
        {
            stateTimer = decisionDelay;
            return;
        }

        HashSet<Vector2Int> blockedCells = CollectBlockingCells();

        float clearance = rvoAgent != null ? rvoAgent.radius + 0.05f : 0f;

        List<Vector2> path = AStarPathfinder2D.FindPath(
            grid,
            rb.position,
            bestAction.transform.position,
            blockedCells,
            clearance
        );

        if (path == null || path.Count == 0)
        {
            bestAction.Release(this);
            stateTimer = decisionDelay;
            return;
        }

        currentAction = bestAction;
        currentPath = path;
        pathIndex = 0;
        stuckTimer = 0f;
        nearSlotTimer = 0f;
        stuckCheckPos = rb.position;
        state = WorkerState.Moving;
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

        if (actionPoint.energyChange > 0) score += actionPoint.energyChange * energyUrgency * 3f;
        if (actionPoint.focusChange > 0) score += actionPoint.focusChange * focusUrgency * 3f;
        if (actionPoint.socialChange > 0) score += actionPoint.socialChange * socialUrgency * 3f;

        if (actionPoint.CurrentUsers > 0 && actionPoint.actionType == OfficeActionType.ChatSpot)
        {
            score += 40f;
        }

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

    private Vector2 CurrentPathTarget()
    {
        if (pathIndex == currentPath.Count - 1 && currentAction != null)
            return currentAction.GetTargetPosition(this);

        return currentPath[pathIndex];
    }

    private bool SlotApproachBlocked(Vector2 toSlot, float dist)
    {
        if (rvoAgent == null || RVOSimulator.Instance == null || dist < 1e-4f)
            return false;

        Vector2 dir = toSlot / dist;

        foreach (RVOAgent b in RVOSimulator.Instance.Agents)
        {
            if (b == rvoAgent || b.owner == null || b.owner == this)
                continue;

            Vector2 toB = b.owner.GetPosition() - rb.position;
            float along = Vector2.Dot(toB, dir);
            if (along <= 0f || along >= dist)
                continue;

            float perp = Mathf.Abs(toB.x * dir.y - toB.y * dir.x);
            float comb = rvoAgent.radius + b.radius + 0.02f;
            if (perp < comb)
                return true;
        }

        return false;
    }

    private bool ShouldYield(Vector2 toTarget, float dist)
    {
        if (rvoAgent == null || RVOSimulator.Instance == null || dist < 1e-4f)
            return false;

        Vector2 myDir = toTarget / dist;
        Vector2 myPos = rb.position;
        int myId = GetInstanceID();

        foreach (RVOAgent b in RVOSimulator.Instance.Agents)
        {
            if (b == rvoAgent || b.owner == null || b.owner == this)
                continue;
            if (!b.active)
                continue;

            AIWorkerAgent other = b.owner;
            Vector2 toOther = other.GetPosition() - myPos;
            float d = toOther.magnitude;
            if (d > yieldDistance || d < 1e-4f)
                continue;

            if (Vector2.Dot(toOther, myDir) <= 0f)
                continue;

            Vector2 otherVel = b.velocity;
            if (otherVel.sqrMagnitude < 1e-6f)
                continue;
            Vector2 otherVelDir = otherVel.normalized;
            Vector2 toMeDir = (-toOther).normalized;
            if (Vector2.Dot(otherVelDir, toMeDir) <= 0.5f)
                continue;

            if (other.GetInstanceID() < myId)
                return true;
        }

        return false;
    }

    private void AdvancePath()
    {
        if (currentPath == null || pathIndex >= currentPath.Count)
        {
            StartActing();
            return;
        }

        while (pathIndex < currentPath.Count)
        {
            Vector2 target = CurrentPathTarget();
            float dist = Vector2.Distance(rb.position, target);
            bool isLast = pathIndex == currentPath.Count - 1;
            float threshold = isLast ? slotArriveDistance : arriveDistance;

            if (dist <= threshold)
            {
                pathIndex++;
                continue;
            }

            if (!isLast && pathIndex + 1 < currentPath.Count)
            {
                Vector2 pathDir = currentPath[pathIndex + 1] - currentPath[pathIndex];
                Vector2 toTarget = target - rb.position;

                if (pathDir.sqrMagnitude > 1e-6f && Vector2.Dot(toTarget, pathDir) < 0f)
                {
                    pathIndex++;
                    continue;
                }
            }

            break;
        }

        if (pathIndex >= currentPath.Count)
        {
            StartActing();
        }
    }

    private void StartActing()
    {
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

        state = WorkerState.Thinking;
        stateTimer = decisionDelay;
    }

    private HashSet<Vector2Int> CollectBlockingCells()
    {
        HashSet<Vector2Int> blocked = new HashSet<Vector2Int>();

        if (grid == null || RVOSimulator.Instance == null || rvoAgent == null)
            return blocked;

        float myRadius = rvoAgent.radius;

        foreach (RVOAgent ra in RVOSimulator.Instance.Agents)
        {
            if (ra == null || ra.owner == null || ra.owner == this)
                continue;

            if (!ra.owner.IsBlocking)
                continue;

            Vector2 otherPos = ra.owner.GetPosition();
            float blockRadius = myRadius + ra.radius + 0.05f;
            Vector2Int centerCell = grid.WorldToCell(otherPos);
            int range = Mathf.CeilToInt(blockRadius / grid.cellSize) + 1;

            for (int dx = -range; dx <= range; dx++)
            {
                for (int dy = -range; dy <= range; dy++)
                {
                    Vector2Int c = new Vector2Int(centerCell.x + dx, centerCell.y + dy);
                    if (!grid.InBounds(c))
                        continue;

                    if (Vector2.Distance(grid.CellToWorld(c), otherPos) < blockRadius)
                        blocked.Add(c);
                }
            }
        }

        return blocked;
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
        stuckTimer = 0f;
        nearSlotTimer = 0f;

        state = WorkerState.Thinking;
        stateTimer = decisionDelay;
    }

    public void ApplyEffects(
        float energyChange,
        float focusChange,
        float socialChange,
        float productivityChange
    )
    {
        energy = Mathf.Clamp(energy + energyChange, 0f, 100f);
        focus = Mathf.Clamp(focus + focusChange, 0f, 100f);
        social = Mathf.Clamp(social + socialChange, 0f, 100f);
        productivity = Mathf.Max(0f, productivity + productivityChange);
    }
}