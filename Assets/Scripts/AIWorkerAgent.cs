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

    private Rigidbody2D rb;
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

        state = WorkerState.Thinking;
        stateTimer = Random.Range(0.2f, 1f);
    }

    private void Update()
    {
        TickNeeds(Time.deltaTime);

        if (state == WorkerState.Thinking)
        {
            stateTimer -= Time.deltaTime;

            if (stateTimer <= 0f)
                DecideNextAction();
        }
        else if (state == WorkerState.Acting)
        {
            stateTimer -= Time.deltaTime;

            if (stateTimer <= 0f)
                FinishAction();
        }
    }

    private void FixedUpdate()
    {
        if (state == WorkerState.Moving)
        {
            MoveAlongPath();
        }
        else if (state == WorkerState.Acting)
        {
            // If someone leaves the group, their index changes
            if (currentAction != null)
            {
                Vector2 correctPos = currentAction.GetTargetPosition(this);
                if (Vector2.Distance(rb.position, correctPos) > 0.05f)
                {
                    rb.MovePosition(Vector2.MoveTowards(
                        rb.position,
                        correctPos,
                        speed * 0.5f * Time.fixedDeltaTime // Slide slower
                    ));
                }
            }
        }
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

        List<Vector2> path = AStarPathfinder2D.FindPath(
            grid,
            rb.position,
            bestAction.transform.position
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

    private void MoveAlongPath()
    {
        if (currentPath == null || pathIndex >= currentPath.Count)
        {
            StartActing();
            return;
        }

        Vector2 GetCurrentTarget()
        {
            if (pathIndex == currentPath.Count - 1 && currentAction != null)
                return currentAction.GetTargetPosition(this);

            return currentPath[pathIndex];
        }

        // Check if we reached the current waypoint
        while (
            pathIndex < currentPath.Count &&
            Vector2.Distance(rb.position, GetCurrentTarget()) <= arriveDistance
        )
        {
            pathIndex++;
        }

        if (pathIndex >= currentPath.Count)
        {
            StartActing();
            return;
        }

        Vector2 newPosition = Vector2.MoveTowards(
            rb.position,
            GetCurrentTarget(),
            speed * Time.fixedDeltaTime
        );

        rb.MovePosition(newPosition);
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