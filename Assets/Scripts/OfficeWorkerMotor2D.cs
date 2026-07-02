using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class OfficeWorkerMotor2D : MonoBehaviour
{
    public float acceleration = 8f;
    public float braking = 10f;
    public float neighborLookahead = 0.75f;
    public float passingClearanceMultiplier = 2.8f;
    public float sideStepDistance = 0.35f;
    public float encounterDistance = 1.25f;
    public float encounterSideBias = 0.7f;

    private readonly List<AIWorkerAgent> nearbyWorkers = new List<AIWorkerAgent>();

    private Rigidbody2D rb;
    private OfficeEncounterCoordinator2D encounters;
    private Vector2 velocity;

    public Vector2 Velocity => velocity;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
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
        float desiredSpeed = Mathf.Min(maxSpeed, dist / Mathf.Max(deltaTime, 0.001f));
        Vector2 desiredVelocity = desiredDir * desiredSpeed;

        desiredVelocity = ApplyLocalAvoidance(owner, grid, crowd, pos, desiredVelocity, radius);

        float response = desiredVelocity.sqrMagnitude > velocity.sqrMagnitude ? acceleration : braking;
        velocity = Vector2.MoveTowards(velocity, desiredVelocity, response * deltaTime);

        Vector2 next = pos + velocity * deltaTime;
        if (!CanOccupy(owner, grid, crowd, pos, next, radius))
        {
            Vector2 sidestep = FindSideStep(owner, grid, crowd, pos, desiredDir, radius);
            if (sidestep.sqrMagnitude > 0f)
            {
                velocity = Vector2.MoveTowards(velocity, sidestep.normalized * maxSpeed * 0.5f, braking * deltaTime);
                next = pos + velocity * deltaTime;
            }
            else
            {
                Stop();
                return false;
            }
        }

        if (CanOccupy(owner, grid, crowd, pos, next, radius))
        {
            rb.MovePosition(next);
            return true;
        }

        Stop();
        return false;
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
        crowd.GetNearbyWorkers(pos, neighborLookahead + radius * 2f, owner, nearbyWorkers);

        foreach (AIWorkerAgent other in nearbyWorkers)
        {
            Vector2 toOther = other.GetPosition() - pos;
            float distance = toOther.magnitude;
            if (distance <= 0.001f)
                continue;

            Vector2 otherVelocity = other.CurrentVelocity;
            bool inFront = Vector2.Dot(desiredDir, toOther / distance) > 0.25f;
            bool closing = Vector2.Dot(desiredVelocity - otherVelocity, toOther) > 0f;
            if (!inFront || !closing)
                continue;

            bool headOnOrCrossing = otherVelocity.sqrMagnitude > 0.01f &&
                                    Vector2.Dot(desiredDir, otherVelocity.normalized) < -0.25f;
            if (headOnOrCrossing && distance <= encounterDistance)
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
                    Vector2 candidate = pos + decision.sideDirection * sideStepDistance;
                    if (grid.IsBodyPositionClear(candidate, radius) &&
                        crowd.IsWorkerSpaceFree(candidate, radius, owner))
                    {
                        desiredVelocity = (desiredDir + decision.sideDirection * encounterSideBias).normalized *
                                          desiredVelocity.magnitude *
                                          decision.speedMultiplier;
                    }
                    else
                    {
                        desiredVelocity *= decision.speedMultiplier;
                    }
                }
                else if (decision.role == OfficeEncounterRole.Yielder)
                {
                    desiredVelocity *= decision.speedMultiplier;
                }

                continue;
            }

            float clearance = grid.GetClearance(grid.WorldToCell(pos));
            bool canPass = clearance >= radius * passingClearanceMultiplier;
            if (canPass)
            {
                float sideSign = owner.GetInstanceID() < other.GetInstanceID() ? -1f : 1f;
                Vector2 side = new Vector2(-desiredDir.y, desiredDir.x) * sideSign;
                Vector2 candidate = pos + side * sideStepDistance;
                if (grid.IsBodyPositionClear(candidate, radius) && crowd.IsWorkerSpaceFree(candidate, radius, owner))
                    desiredVelocity = (desiredDir + side * 0.55f).normalized * desiredVelocity.magnitude;
            }
            else if (owner.GetRightOfWayPriority() < other.GetRightOfWayPriority())
            {
                desiredVelocity *= 0.15f;
            }
        }

        return desiredVelocity;
    }

    private Vector2 FindSideStep(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 pos,
        Vector2 forward,
        float radius)
    {
        Vector2 side = new Vector2(-forward.y, forward.x);
        Vector2 left = side * sideStepDistance;
        Vector2 right = -left;

        if (CanOccupy(owner, grid, crowd, pos, pos + left, radius))
            return left;

        if (CanOccupy(owner, grid, crowd, pos, pos + right, radius))
            return right;

        return Vector2.zero;
    }

    private bool CanOccupy(
        AIWorkerAgent owner,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 from,
        Vector2 to,
        float radius)
    {
        return grid.CanMoveBody(from, to, radius) &&
               crowd.IsWorkerSpaceFree(to, radius, owner);
    }
}
