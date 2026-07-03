using UnityEngine;
using System.Collections.Generic;

public enum OfficeActionType
{
    WorkDesk,
    CoffeeMachine,
    BreakSpot,
    ChatSpot,
    MeetingRoom
}

public class OfficeActionPoint : MonoBehaviour
{
    public OfficeActionType actionType;

    [Header("Action Settings")]
    public float useTime = 3f;
    public float baseScore = 10f;

    [Tooltip("How many agents can use this at the same time? (e.g. ChatSpot = 2, WorkDesk = 1)")]
    public int capacity = 1;

    [Tooltip("Used only when capacity is greater than 1. Keeps users separated around this point.")]
    public float multiUserSlotRadius = 0.5f;

    [Tooltip("For single-user points, offset the target away from the marker. Useful when the marker is centered on a desk but workers should stand beside it.")]
    public Vector2 singleUserTargetOffset = Vector2.zero;

    [Header("Effects After Use")]
    public float energyChange = 0f;
    public float focusChange = 0f;
    public float socialChange = 0f;
    public float productivityChange = 0f;

    // Stable reservation slots. A worker keeps the same slot until Release().
    private readonly Dictionary<AIWorkerAgent, int> reservedSlots = new Dictionary<AIWorkerAgent, int>();
    private readonly Dictionary<AIWorkerAgent, float> departingAgents = new Dictionary<AIWorkerAgent, float>();

    public int CurrentUsers
    {
        get
        {
            return GetOccupiedSlotCount(null);
        }
    }

    public bool IsReservedBy(AIWorkerAgent agent)
    {
        return agent != null && reservedSlots.ContainsKey(agent);
    }

    public bool IsReservedByOther(AIWorkerAgent agent)
    {
        PruneDepartingAgents();

        if (agent != null && reservedSlots.ContainsKey(agent))
            return false;

        return GetOccupiedSlotCount(agent) >= Mathf.Max(1, capacity);
    }

    public bool TryReserve(AIWorkerAgent agent)
    {
        if (agent == null)
            return false;

        PruneDepartingAgents();

        if (reservedSlots.ContainsKey(agent))
            return true;

        departingAgents.Remove(agent);

        int safeCapacity = Mathf.Max(1, capacity);
        if (GetOccupiedSlotCount(agent) >= safeCapacity)
            return false;

        for (int slot = 0; slot < safeCapacity; slot++)
        {
            if (!reservedSlots.ContainsValue(slot))
            {
                reservedSlots.Add(agent, slot);
                return true;
            }
        }

        return false;
    }

    public void Release(AIWorkerAgent agent)
    {
        if (agent != null)
            reservedSlots.Remove(agent);
    }

    public void HoldDepartingAgent(AIWorkerAgent agent, float holdSeconds)
    {
        if (agent == null || Mathf.Max(1, capacity) <= 1)
            return;

        PruneDepartingAgents();
        if (IsInsideDepartureHold(agent))
            departingAgents[agent] = Time.time + Mathf.Max(0.25f, holdSeconds);
    }

    public Vector2 GetTargetPosition(AIWorkerAgent agent)
    {
        int safeCapacity = Mathf.Max(1, capacity);

        if (agent == null || safeCapacity <= 1)
            return (Vector2)transform.position + singleUserTargetOffset;

        if (!reservedSlots.TryGetValue(agent, out int slot))
            return transform.position;

        return GetSlotPosition(slot, safeCapacity);
    }

    private Vector2 GetSlotPosition(int slot, int safeCapacity)
    {
        float angle = slot * (Mathf.PI * 2f / safeCapacity);
        Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * multiUserSlotRadius;
        return (Vector2)transform.position + offset;
    }

    public bool TryGetInteractionBlocker(
        OfficeGrid2D grid,
        Vector2 workerPosition,
        float agentRadius,
        out Vector2 furniturePoint,
        out Vector2 workerPoint,
        out float radius)
    {
        furniturePoint = transform.position;
        workerPoint = workerPosition;
        radius = 0f;

        if (actionType != OfficeActionType.WorkDesk || grid == null)
            return false;

        Vector2 actionPosition = transform.position;
        float searchRadius = Mathf.Max(2f, Mathf.Max(agentRadius * 8f, multiUserSlotRadius * 3f));
        Collider2D[] obstacles = Physics2D.OverlapCircleAll(actionPosition, searchRadius, grid.obstacleMask);
        Collider2D bestObstacle = null;
        float bestScore = float.PositiveInfinity;

        for (int i = 0; i < obstacles.Length; i++)
        {
            Collider2D obstacle = obstacles[i];
            if (obstacle == null || !obstacle.enabled)
                continue;

            Vector2 actionClosest = obstacle.ClosestPoint(actionPosition);
            Vector2 workerClosest = obstacle.ClosestPoint(workerPosition);
            float score = (actionClosest - actionPosition).sqrMagnitude +
                          (workerClosest - workerPosition).sqrMagnitude * 0.2f;
            if (score >= bestScore)
                continue;

            bestScore = score;
            bestObstacle = obstacle;
        }

        if (bestObstacle == null)
            return false;

        furniturePoint = bestObstacle.ClosestPoint(workerPosition);
        workerPoint = workerPosition;
        float gap = Vector2.Distance(furniturePoint, workerPoint);
        if (gap <= agentRadius * 0.35f || gap > searchRadius)
            return false;

        radius = Mathf.Max(0.18f, agentRadius * 0.9f);
        return true;
    }

    public bool TryGetFullActionBlocker(float agentRadius, out Vector2 center, out float radius)
    {
        center = transform.position;
        radius = 0f;

        int safeCapacity = Mathf.Max(1, capacity);
        if (safeCapacity <= 1 || GetOccupiedSlotCount(null) < safeCapacity)
            return false;

        radius = Mathf.Max(multiUserSlotRadius + agentRadius, agentRadius * 2.2f);
        return true;
    }

    public bool NeedsDepartureWaypoint(Vector2 workerPosition, Vector2 destination, float agentRadius)
    {
        int safeCapacity = Mathf.Max(1, capacity);
        if (safeCapacity <= 1)
            return false;

        Vector2 center = transform.position;
        float activeRadius = Mathf.Max(multiUserSlotRadius + agentRadius * 1.4f, agentRadius * 2f);
        if (Vector2.Distance(workerPosition, center) > activeRadius)
            return false;

        float innerRadius = Mathf.Max(agentRadius * 1.7f, multiUserSlotRadius * 0.65f);
        bool routeCutsThroughPoint = DistancePointToSegment(center, workerPosition, destination) <= innerRadius;
        bool otherOccupantsStillClearing = GetOccupiedSlotCount(null) > 1;
        return routeCutsThroughPoint || otherOccupantsStillClearing;
    }

    public bool TryGetDepartureWaypoint(
        Vector2 workerPosition,
        Vector2 destination,
        OfficeGrid2D grid,
        float agentRadius,
        out Vector2 waypoint)
    {
        waypoint = workerPosition;

        if (grid == null || !NeedsDepartureWaypoint(workerPosition, destination, agentRadius))
            return false;

        Vector2 center = transform.position;
        Vector2 direction = workerPosition - center;
        if (direction.sqrMagnitude <= 0.0001f)
            direction = workerPosition - destination;

        if (direction.sqrMagnitude <= 0.0001f)
            direction = Vector2.right;

        float departureRadius = Mathf.Max(
            multiUserSlotRadius + agentRadius * 2.75f,
            multiUserSlotRadius * 1.8f);
        float minDistanceFromGroup = multiUserSlotRadius + agentRadius * 1.8f;
        return TryFindDirectionalDepartureWaypoint(
            grid,
            agentRadius,
            center,
            direction.normalized,
            departureRadius,
            minDistanceFromGroup,
            out waypoint);
    }

    public bool GetApproachWaypoints(AIWorkerAgent agent, OfficeGrid2D grid, float agentRadius, List<Vector2> waypoints)
    {
        if (waypoints == null)
            return false;

        waypoints.Clear();

        int safeCapacity = Mathf.Max(1, capacity);
        if (agent == null || grid == null || safeCapacity <= 1 || !reservedSlots.ContainsKey(agent))
            return true;

        if (CountOtherReservedAgents(agent) < 1)
            return true;

        Vector2 center = transform.position;
        Vector2 start = agent.GetPosition();
        Vector2 target = GetTargetPosition(agent);
        if (!ApproachCutsThroughOccupiedSide(agent, start, target, center, agentRadius))
            return true;

        Vector2 startDir = DirectionFromCenter(center, start, target);
        Vector2 targetDir = DirectionFromCenter(center, target, start);
        float signedDelta = SignedShortestAngle(startDir, targetDir);
        float primaryTurn = Mathf.Abs(signedDelta) > 0.001f
            ? Mathf.Sign(signedDelta)
            : StableTurnSign(agent);

        if (TryBuildArcWaypoints(grid, agentRadius, center, startDir, targetDir, primaryTurn, waypoints))
            return true;

        return TryBuildArcWaypoints(grid, agentRadius, center, startDir, targetDir, -primaryTurn, waypoints);
    }

    public void ApplyTo(AIWorkerAgent agent)
    {
        agent.ApplyEffects(
            energyChange,
            focusChange,
            socialChange,
            productivityChange
        );
    }

    private bool TryFindDirectionalDepartureWaypoint(
        OfficeGrid2D grid,
        float agentRadius,
        Vector2 center,
        Vector2 outwardDirection,
        float departureRadius,
        float minDistanceFromGroup,
        out Vector2 waypoint)
    {
        waypoint = center;
        float[] angleOffsets = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };

        for (int i = 0; i < angleOffsets.Length; i++)
        {
            Vector2 direction = Rotate(outwardDirection, angleOffsets[i] * Mathf.Deg2Rad).normalized;
            Vector2 candidate = center + direction * departureRadius;
            if (!grid.TryFindNearestWalkable(candidate, agentRadius, out Vector2 safeCandidate))
                continue;

            Vector2 safeDirection = safeCandidate - center;
            if (safeDirection.sqrMagnitude <= 0.0001f)
                continue;

            if (Vector2.Dot(safeDirection.normalized, outwardDirection) < 0.35f)
                continue;

            if (Vector2.Distance(safeCandidate, center) < minDistanceFromGroup)
                continue;

            waypoint = safeCandidate;
            return true;
        }

        return false;
    }

    private int GetOccupiedSlotCount(AIWorkerAgent requester)
    {
        PruneDepartingAgents();

        int count = reservedSlots.Count;
        foreach (AIWorkerAgent departingAgent in departingAgents.Keys)
        {
            if (departingAgent != null && departingAgent != requester && !reservedSlots.ContainsKey(departingAgent))
                count++;
        }

        return count;
    }

    private void PruneDepartingAgents()
    {
        if (departingAgents.Count == 0)
            return;

        List<AIWorkerAgent> expiredAgents = null;
        foreach (KeyValuePair<AIWorkerAgent, float> entry in departingAgents)
        {
            AIWorkerAgent agent = entry.Key;
            if (agent == null || Time.time > entry.Value || !IsInsideDepartureHold(agent))
            {
                if (expiredAgents == null)
                    expiredAgents = new List<AIWorkerAgent>();

                expiredAgents.Add(agent);
            }
        }

        if (expiredAgents == null)
            return;

        for (int i = 0; i < expiredAgents.Count; i++)
            departingAgents.Remove(expiredAgents[i]);
    }

    private bool IsInsideDepartureHold(AIWorkerAgent agent)
    {
        if (agent == null)
            return false;

        float holdRadius = Mathf.Max(multiUserSlotRadius + agent.avoidanceRadius * 2.4f, multiUserSlotRadius * 2f);
        return Vector2.Distance(agent.GetPosition(), transform.position) <= holdRadius;
    }

    private bool ApproachCutsThroughOccupiedSide(
        AIWorkerAgent agent,
        Vector2 start,
        Vector2 target,
        Vector2 center,
        float agentRadius)
    {
        float groupRadius = Mathf.Max(agentRadius * 1.8f, multiUserSlotRadius * 0.75f);
        if (DistancePointToSegment(center, start, target) <= groupRadius)
            return true;

        foreach (KeyValuePair<AIWorkerAgent, int> entry in reservedSlots)
        {
            AIWorkerAgent other = entry.Key;
            if (other == null || other == agent)
                continue;

            Vector2 slotPosition = GetSlotPosition(entry.Value, Mathf.Max(1, capacity));
            float protectedRadius = Mathf.Max(agentRadius * 2.15f, multiUserSlotRadius * 0.9f);
            if (DistancePointToSegment(slotPosition, start, target) <= protectedRadius)
                return true;

            if (DistancePointToSegment(other.GetPosition(), start, target) <= protectedRadius)
                return true;
        }

        return false;
    }

    private int CountOtherReservedAgents(AIWorkerAgent agent)
    {
        int count = 0;
        foreach (AIWorkerAgent reservedAgent in reservedSlots.Keys)
        {
            if (reservedAgent != null && reservedAgent != agent)
                count++;
        }

        return count;
    }

    private bool TryBuildArcWaypoints(
        OfficeGrid2D grid,
        float agentRadius,
        Vector2 center,
        Vector2 startDir,
        Vector2 targetDir,
        float turnSign,
        List<Vector2> waypoints)
    {
        float delta = ArcDelta(startDir, targetDir, turnSign);
        if (delta <= 0.001f)
            return false;

        float approachRadius = Mathf.Max(
            multiUserSlotRadius + agentRadius * 2.75f,
            multiUserSlotRadius * 1.8f);
        int steps = Mathf.Clamp(Mathf.CeilToInt(delta / (Mathf.PI * 0.25f)), 2, 8);
        float startAngle = Mathf.Atan2(startDir.y, startDir.x);

        waypoints.Clear();
        for (int i = 1; i < steps; i++)
        {
            float angle = startAngle + turnSign * delta * (i / (float)steps);
            Vector2 candidate = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * approachRadius;
            if (!TryAddWaypoint(grid, agentRadius, center, candidate, waypoints))
            {
                waypoints.Clear();
                return false;
            }
        }

        return waypoints.Count > 0;
    }

    private bool TryAddWaypoint(
        OfficeGrid2D grid,
        float agentRadius,
        Vector2 center,
        Vector2 candidate,
        List<Vector2> waypoints)
    {
        if (!grid.TryFindNearestWalkable(candidate, agentRadius, out Vector2 safeCandidate))
            return false;

        float minDistanceFromGroup = multiUserSlotRadius + agentRadius * 1.8f;
        if (Vector2.Distance(safeCandidate, center) < minDistanceFromGroup)
            return false;

        if (waypoints.Count == 0 || Vector2.Distance(waypoints[waypoints.Count - 1], safeCandidate) > 0.05f)
            waypoints.Add(safeCandidate);

        return true;
    }

    private static Vector2 DirectionFromCenter(Vector2 center, Vector2 position, Vector2 fallback)
    {
        Vector2 direction = position - center;
        if (direction.sqrMagnitude <= 0.0001f)
            direction = center - fallback;

        if (direction.sqrMagnitude <= 0.0001f)
            direction = Vector2.right;

        return direction.normalized;
    }

    private static float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float abLengthSq = ab.sqrMagnitude;
        if (abLengthSq <= 0.0001f)
            return Vector2.Distance(point, a);

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / abLengthSq);
        return Vector2.Distance(point, a + ab * t);
    }

    private static float SignedShortestAngle(Vector2 from, Vector2 to)
    {
        return Mathf.Atan2(Cross(from, to), Vector2.Dot(from, to));
    }

    private static float ArcDelta(Vector2 from, Vector2 to, float turnSign)
    {
        float fromAngle = Mathf.Atan2(from.y, from.x);
        float toAngle = Mathf.Atan2(to.y, to.x);
        float delta = toAngle - fromAngle;

        if (turnSign > 0f)
        {
            while (delta < 0f)
                delta += Mathf.PI * 2f;
        }
        else
        {
            while (delta > 0f)
                delta -= Mathf.PI * 2f;
            delta = -delta;
        }

        return delta;
    }

    private static float StableTurnSign(AIWorkerAgent agent)
    {
        return agent.GetInstanceID() % 2 == 0 ? 1f : -1f;
    }

    private static Vector2 Rotate(Vector2 vector, float radians)
    {
        float sin = Mathf.Sin(radians);
        float cos = Mathf.Cos(radians);
        return new Vector2(
            vector.x * cos - vector.y * sin,
            vector.x * sin + vector.y * cos);
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private void OnDrawGizmos()
    {
        int currentUsers = reservedSlots != null ? reservedSlots.Count : 0;

        if (currentUsers == 0) Gizmos.color = Color.green;
        else if (currentUsers < Mathf.Max(1, capacity)) Gizmos.color = Color.yellow;
        else Gizmos.color = Color.red;

        Gizmos.DrawWireSphere(transform.position, 0.2f);

        int safeCapacity = Mathf.Max(1, capacity);
        if (safeCapacity > 1)
        {
            Gizmos.color = Color.cyan;
            for (int slot = 0; slot < safeCapacity; slot++)
            {
                float angle = slot * (Mathf.PI * 2f / safeCapacity);
                Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * multiUserSlotRadius;
                Gizmos.DrawWireSphere((Vector2)transform.position + offset, 0.12f);
            }
        }
    }
}
