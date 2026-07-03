using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class OfficeWorkerMotor2D : MonoBehaviour
{
    public float acceleration = 8f;
    public float braking = 10f;
    public float neighborLookahead = 1.1f;
    public float passingClearanceMultiplier = 2.6f;
    public float sideStepDistance = 0.4f;
    public float encounterDistance = 1.35f;
    public float encounterSideBias = 0.85f;

    [Header("Personal Space")]
    [Tooltip("Desired center-to-center distance between two workers, expressed as a multiple of the worker radius.")]
    public float personalSpaceMultiplier = 2.35f;
    [Tooltip("How strongly workers drift away when another worker enters their personal space.")]
    public float personalSpacePush = 1.2f;
    [Tooltip("How far ahead a worker starts matching the pace of someone in the same lane.")]
    public float followDistanceMultiplier = 3.2f;
    [Tooltip("Lowest speed scale used while following or yielding. Higher values prevent polite deadlocks.")]
    public float minimumMovingSpeedScale = 0.22f;
    [Tooltip("Speed scale used for short escape steps when the direct path is blocked.")]
    public float blockedEscapeSpeedMultiplier = 0.55f;

    private readonly List<AIWorkerAgent> nearbyWorkers = new List<AIWorkerAgent>();
    private readonly List<AIWorkerAgent> clearanceWorkers = new List<AIWorkerAgent>();

    private Rigidbody2D rb;
    private OfficeEncounterCoordinator2D encounters;
    private Vector2 velocity;

    public Vector2 Velocity => velocity;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        Collider2D bodyCollider = GetComponent<Collider2D>();
        bodyCollider.isTrigger = true;

        rb.bodyType = RigidbodyType2D.Kinematic;
        rb.gravityScale = 0f;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        encounters = OfficeEncounterCoordinator2D.Ensure();
    }

    public void Stop()
    {
        velocity = Vector2.zero;
    }

    public bool MoveToward(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 target,
        float radius,
        float maxSpeed,
        float deltaTime)
    {
        if (owner == null || grid == null || crowd == null || deltaTime <= 0f)
            return false;

        Vector2 pos = rb.position;
        Vector2 toTarget = target - pos;
        float dist = toTarget.magnitude;
        if (dist <= 0.001f)
        {
            Stop();
            return true;
        }

        Vector2 desiredDir = toTarget / dist;
        if (TryGetOverlapEscapeVelocity(owner, grid, crowd, pos, target, desiredDir, radius, maxSpeed, deltaTime, out Vector2 overlapEscapeVelocity))
        {
            Vector2 overlapNext = pos + overlapEscapeVelocity * deltaTime;
            if (grid.CanRecoverBody(pos, overlapNext, radius) &&
                crowd.CanRecoverWorkerOverlap(pos, overlapNext, radius, owner))
            {
                velocity = overlapEscapeVelocity;
                rb.MovePosition(overlapNext);
                return true;
            }
        }

        float desiredSpeed = Mathf.Min(maxSpeed, dist / Mathf.Max(deltaTime, 0.001f));
        Vector2 desiredVelocity = desiredDir * desiredSpeed;

        desiredVelocity = ApplyLocalAvoidance(owner, grid, crowd, pos, desiredVelocity, radius);

        float response = desiredVelocity.sqrMagnitude > velocity.sqrMagnitude ? acceleration : braking;
        velocity = Vector2.MoveTowards(velocity, desiredVelocity, response * deltaTime);

        Vector2 next = pos + velocity * deltaTime;
        if (!CanOccupy(owner, grid, crowd, pos, next, radius) &&
            !CanRecoverOccupancy(owner, grid, crowd, pos, next, radius))
        {
            Vector2 escapeVelocity = FindFallbackVelocity(owner, grid, crowd, pos, target, desiredDir, radius, maxSpeed);
            if (escapeVelocity.sqrMagnitude > 0f)
            {
                velocity = escapeVelocity;
                next = pos + velocity * deltaTime;
            }
            else
            {
                Stop();
                return false;
            }
        }

        if (CanOccupy(owner, grid, crowd, pos, next, radius) ||
            CanRecoverOccupancy(owner, grid, crowd, pos, next, radius))
        {
            rb.MovePosition(next);
            return true;
        }

        Stop();
        return false;
    }

    private bool TryGetOverlapEscapeVelocity(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 pos,
        Vector2 target,
        Vector2 desiredDir,
        float radius,
        float maxSpeed,
        float deltaTime,
        out Vector2 escapeVelocity)
    {
        escapeVelocity = Vector2.zero;

        float currentPenalty = crowd.GetWorkerOverlapPenalty(pos, radius, owner);
        if (currentPenalty <= 0.0001f)
            return false;

        float minDist = radius * 2f;
        Vector2 separation = Vector2.zero;
        crowd.GetNearbyWorkers(pos, minDist + radius, owner, nearbyWorkers);

        foreach (AIWorkerAgent other in nearbyWorkers)
        {
            Vector2 away = pos - other.GetPosition();
            float distance = away.magnitude;
            if (distance <= 0.001f)
            {
                away = DeterministicSeparation(owner, other);
                distance = 0.001f;
            }

            float overlap = minDist - distance;
            if (overlap <= 0f)
                continue;

            separation += away.normalized * Mathf.Max(0.05f, overlap);
        }

        if (separation.sqrMagnitude <= 0.0001f)
            separation = desiredDir.sqrMagnitude > 0.0001f ? desiredDir : Vector2.right;

        Vector2 primary = separation.normalized;
        Vector2 right = RightOf(primary);
        Vector2 awayFromGoal = (primary - desiredDir * 0.35f).normalized;
        Vector2[] directions =
        {
            primary,
            (primary + right * 0.55f).normalized,
            (primary - right * 0.55f).normalized,
            awayFromGoal,
            right,
            -right,
            -desiredDir
        };

        float escapeSpeed = maxSpeed * Mathf.Max(blockedEscapeSpeedMultiplier, 0.85f);
        float stepDistance = Mathf.Max(escapeSpeed * deltaTime, radius * 0.08f);
        float bestScore = float.NegativeInfinity;
        Vector2 bestVelocity = Vector2.zero;

        for (int i = 0; i < directions.Length; i++)
        {
            Vector2 direction = directions[i];
            if (direction.sqrMagnitude <= 0.0001f)
                continue;

            direction.Normalize();
            Vector2 candidate = pos + direction * stepDistance;
            if (!grid.CanRecoverBody(pos, candidate, radius) ||
                !crowd.CanRecoverWorkerOverlap(pos, candidate, radius, owner))
                continue;

            float candidatePenalty = crowd.GetWorkerOverlapPenalty(candidate, radius, owner);
            float improvement = currentPenalty - candidatePenalty;
            float score = improvement * 8f;
            score += Vector2.Dot(direction, desiredDir) * 0.2f;
            score -= Vector2.Distance(candidate, target) * 0.02f;

            if (score > bestScore)
            {
                bestScore = score;
                bestVelocity = direction * escapeSpeed;
            }
        }

        if (bestVelocity.sqrMagnitude <= 0.0001f)
            return false;

        escapeVelocity = bestVelocity;
        return true;
    }

    private Vector2 ApplyLocalAvoidance(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 pos,
        Vector2 desiredVelocity,
        float radius)
    {
        if (desiredVelocity.sqrMagnitude <= 0.0001f)
            return Vector2.zero;

        Vector2 desiredDir = desiredVelocity.normalized;
        Vector2 right = RightOf(desiredDir);
        Vector2 steeringDir = desiredDir;
        float desiredSpeed = desiredVelocity.magnitude;
        float speedScale = 1f;
        float personalSpace = Mathf.Max(radius * personalSpaceMultiplier, radius * 2f + 0.05f);
        float awarenessRange = Mathf.Max(neighborLookahead, radius * followDistanceMultiplier) + radius * 2f;

        crowd.GetNearbyWorkers(pos, awarenessRange, owner, nearbyWorkers);

        foreach (AIWorkerAgent other in nearbyWorkers)
        {
            Vector2 toOther = other.GetPosition() - pos;
            float distance = toOther.magnitude;
            if (distance <= 0.001f)
            {
                toOther = DeterministicSeparation(owner, other) * 0.001f;
                distance = 0.001f;
            }

            Vector2 toOtherDir = toOther / distance;
            Vector2 awayFromOther = -toOtherDir;
            Vector2 otherVelocity = other.CurrentVelocity;
            bool otherMoving = otherVelocity.sqrMagnitude > 0.01f;

            if (distance < personalSpace)
            {
                float pressure = 1f - Mathf.Clamp01(distance / personalSpace);
                steeringDir += awayFromOther * personalSpacePush * pressure;
                speedScale = Mathf.Min(speedScale, Mathf.Lerp(1f, 0.55f, pressure));
            }

            float forwardAmount = Vector2.Dot(desiredDir, toOtherDir);
            float lateralDistance = Mathf.Abs(Vector2.Dot(right, toOther));
            bool inFront = forwardAmount > 0.05f && lateralDistance < radius * 2.4f;
            bool closing = Vector2.Dot(desiredVelocity - otherVelocity, toOther) > 0f;
            bool headOnOrCrossing = otherMoving && Vector2.Dot(desiredDir, otherVelocity.normalized) < -0.25f;

            if (headOnOrCrossing && closing && distance <= encounterDistance)
            {
                OfficeEncounterDecision decision = encounters.GetEncounterDecision(
                    owner,
                    other,
                    grid,
                    crowd,
                    desiredDir,
                    radius,
                    sideStepDistance);

                if (decision.role == OfficeEncounterRole.MutualRightPass ||
                    decision.role == OfficeEncounterRole.Passer)
                {
                    if (IsSideOpen(owner, grid, crowd, pos, decision.sideDirection, radius))
                        steeringDir += decision.sideDirection * encounterSideBias;

                    speedScale = Mathf.Min(speedScale, decision.speedMultiplier);
                }
                else if (decision.role == OfficeEncounterRole.Yielder)
                {
                    if (IsSideOpen(owner, grid, crowd, pos, decision.sideDirection, radius))
                        steeringDir += decision.sideDirection * encounterSideBias * 0.35f;

                    speedScale = Mathf.Min(speedScale, decision.speedMultiplier);
                }

                continue;
            }

            if (!inFront)
            {
                if (closing && distance < personalSpace * 1.25f)
                {
                    steeringDir += awayFromOther * 0.35f;
                    speedScale = Mathf.Min(speedScale, 0.8f);
                }

                continue;
            }

            bool sameDirection = otherMoving && Vector2.Dot(desiredDir, otherVelocity.normalized) > 0.35f;
            bool slowOrStoppedAhead = other.IsBlocking || !otherMoving;
            float followDistance = Mathf.Max(radius * followDistanceMultiplier, radius * 2f + 0.15f);

            if (sameDirection || slowOrStoppedAhead)
            {
                float followPressure = 1f - Mathf.Clamp01(distance / followDistance);
                if (followPressure > 0f)
                    speedScale = Mathf.Min(speedScale, Mathf.Lerp(1f, minimumMovingSpeedScale, followPressure));

                bool corridorWideEnough = grid.GetClearance(grid.WorldToCell(pos)) >= radius * passingClearanceMultiplier;
                if (corridorWideEnough && IsSideOpen(owner, grid, crowd, pos, right, radius))
                    steeringDir += right * 0.55f * Mathf.Max(0.25f, followPressure);
            }
            else if (closing)
            {
                steeringDir += awayFromOther * 0.45f;
                speedScale = Mathf.Min(speedScale, 0.75f);
            }
        }

        if (steeringDir.sqrMagnitude <= 0.0001f)
            steeringDir = desiredDir;

        speedScale = Mathf.Clamp(speedScale, minimumMovingSpeedScale, 1f);
        return steeringDir.normalized * desiredSpeed * speedScale;
    }

    private Vector2 FindFallbackVelocity(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 pos,
        Vector2 target,
        Vector2 desiredDir,
        float radius,
        float maxSpeed)
    {
        Vector2 right = RightOf(desiredDir);
        Vector2[] directions =
        {
            (desiredDir + right * 1.1f).normalized,
            (desiredDir - right * 0.9f).normalized,
            right,
            -right,
            (-desiredDir + right * 0.45f).normalized,
            (-desiredDir - right * 0.45f).normalized,
            -desiredDir
        };

        float stepDistance = Mathf.Max(sideStepDistance, radius * 1.35f);
        float bestScore = float.NegativeInfinity;
        Vector2 bestDirection = Vector2.zero;

        for (int i = 0; i < directions.Length; i++)
        {
            Vector2 direction = directions[i];
            if (direction.sqrMagnitude <= 0.0001f)
                continue;

            Vector2 candidate = pos + direction * stepDistance;
            if (!CanOccupy(owner, grid, crowd, pos, candidate, radius) &&
                !CanRecoverOccupancy(owner, grid, crowd, pos, candidate, radius))
                continue;

            float score = Vector2.Dot(direction, desiredDir) * 2f;
            score += Vector2.Dot(direction, right) * 0.2f;
            score -= Vector2.Distance(candidate, target) * 0.15f;
            score += GetWorkerClearanceScore(crowd, candidate, radius, owner) * 1.5f;
            score += Mathf.Clamp01(grid.GetClearance(grid.WorldToCell(candidate)) / Mathf.Max(0.01f, radius * passingClearanceMultiplier)) * 0.4f;

            if (Vector2.Dot(direction, desiredDir) < -0.25f)
                score -= 0.75f;

            if (score > bestScore)
            {
                bestScore = score;
                bestDirection = direction;
            }
        }

        if (bestDirection.sqrMagnitude <= 0.0001f)
            return Vector2.zero;

        return bestDirection.normalized * maxSpeed * blockedEscapeSpeedMultiplier;
    }

    private bool IsSideOpen(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 pos,
        Vector2 sideDirection,
        float radius)
    {
        if (sideDirection.sqrMagnitude <= 0.0001f)
            return false;

        Vector2 candidate = pos + sideDirection.normalized * sideStepDistance;
        return grid.IsBodyPhysicallyClear(candidate, radius) &&
               crowd.IsWorkerMoveClear(pos, candidate, radius, owner);
    }

    private float GetWorkerClearanceScore(
        OfficeCrowdCoordinator2D crowd,
        Vector2 position,
        float radius,
        AIWorkerAgent owner)
    {
        float comfortableDistance = Mathf.Max(radius * personalSpaceMultiplier, radius * 2f + 0.05f);
        crowd.GetNearbyWorkers(position, comfortableDistance, owner, clearanceWorkers);

        if (clearanceWorkers.Count == 0)
            return 1f;

        float closest = float.PositiveInfinity;
        foreach (AIWorkerAgent worker in clearanceWorkers)
            closest = Mathf.Min(closest, Vector2.Distance(position, worker.GetPosition()));

        float hardDistance = radius * 2f;
        return Mathf.Clamp01((closest - hardDistance) / Mathf.Max(0.01f, comfortableDistance - hardDistance));
    }

    private bool CanOccupy(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 from,
        Vector2 to,
        float radius)
    {
        return grid.CanMoveBodyPhysically(from, to, radius) &&
               crowd.IsWorkerMoveClear(from, to, radius, owner);
    }

    private bool CanRecoverOccupancy(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 from,
        Vector2 to,
        float radius)
    {
        return grid.CanRecoverBody(from, to, radius) &&
               (crowd.IsWorkerMoveClear(from, to, radius, owner) ||
                crowd.CanRecoverWorkerOverlap(from, to, radius, owner));
    }

    private static Vector2 RightOf(Vector2 forward)
    {
        return new Vector2(forward.y, -forward.x);
    }

    private static Vector2 DeterministicSeparation(AIWorkerAgent owner, AIWorkerAgent other)
    {
        int ownerId = owner.GetInstanceID();
        int otherId = other.GetInstanceID();
        int lowId = Mathf.Min(ownerId, otherId);
        int highId = Mathf.Max(ownerId, otherId);
        uint hash = unchecked((uint)(lowId * 486187739) ^ (uint)(highId * 16777619));
        float angle = (hash % 360u) * Mathf.Deg2Rad;
        Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        return ownerId < otherId ? direction : -direction;
    }
}
