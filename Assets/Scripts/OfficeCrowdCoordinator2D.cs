using System.Collections.Generic;
using UnityEngine;

public class OfficeCrowdCoordinator2D : MonoBehaviour
{
    private struct Reservation
    {
        public AIWorkerAgent owner;
        public float expiresAt;
    }

    private struct SeparationPair
    {
        public int moverId;
        public float expiresAt;
    }

    public static OfficeCrowdCoordinator2D Instance { get; private set; }

    [Header("Crowd Costs")]
    public float activeWorkerCellCost = 6f;
    public float nearbyWorkerCellCost = 2f;
    public float reservationCost = 4f;
    public float reservationLifetime = 1.2f;

    [Header("Emergency Separation")]
    [Tooltip("How many pair-resolution passes run after normal movement each frame.")]
    public int overlapResolutionIterations = 4;
    [Tooltip("Extra spacing kept after emergency worker-body separation.")]
    public float overlapSeparationSkin = 0.015f;
    [Tooltip("Maximum distance an agent can be nudged by one emergency separation step.")]
    public float maxOverlapSeparationStep = 0.12f;
    [Tooltip("How long one worker keeps responsibility for yielding out of an overlap with the same neighbor.")]
    public float separationRoleLifetime = 1.1f;

    private readonly List<AIWorkerAgent> workers = new List<AIWorkerAgent>();
    private readonly Dictionary<Vector2Int, List<Reservation>> reservations = new Dictionary<Vector2Int, List<Reservation>>();
    private readonly Dictionary<long, SeparationPair> separationPairs = new Dictionary<long, SeparationPair>();

    public IReadOnlyList<AIWorkerAgent> Workers => workers;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public static OfficeCrowdCoordinator2D Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new GameObject(nameof(OfficeCrowdCoordinator2D));
        return go.AddComponent<OfficeCrowdCoordinator2D>();
    }

    private void LateUpdate()
    {
        ResolveWorkerOverlaps();
    }

    private void ResolveWorkerOverlaps()
    {
        if (workers.Count < 2)
            return;

        int iterations = Mathf.Max(1, overlapResolutionIterations);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            bool movedAny = false;

            for (int i = workers.Count - 1; i >= 0; i--)
            {
                AIWorkerAgent a = workers[i];
                if (a == null)
                {
                    workers.RemoveAt(i);
                    continue;
                }

                for (int j = i - 1; j >= 0; j--)
                {
                    AIWorkerAgent b = workers[j];
                    if (b == null)
                        continue;

                    if (ResolveWorkerOverlapPair(a, b))
                        movedAny = true;
                }
            }

            if (!movedAny)
                return;
        }
    }

    private bool ResolveWorkerOverlapPair(AIWorkerAgent a, AIWorkerAgent b)
    {
        Vector2 aPos = a.GetPosition();
        Vector2 bPos = b.GetPosition();
        float minDistance = a.avoidanceRadius + b.avoidanceRadius + Mathf.Max(0f, overlapSeparationSkin);
        Vector2 fromAtoB = bPos - aPos;
        float distance = fromAtoB.magnitude;
        if (distance >= minDistance)
            return false;

        Vector2 pairDirection = distance > 0.0001f
            ? fromAtoB / distance
            : DeterministicPairDirection(a, b);
        float correction = Mathf.Min(minDistance - distance, Mathf.Max(0.01f, maxOverlapSeparationStep));

        AIWorkerAgent mover = GetSeparationMover(a, b);
        AIWorkerAgent holder = mover == a ? b : a;
        Vector2 moverAway = mover == a ? -pairDirection : pairDirection;
        Vector2 holderAway = mover == a ? pairDirection : -pairDirection;
        Vector2 holderPosition = holder.GetPosition();
        Vector2 moverPosition = mover.GetPosition();

        if (TryMoveWorkerAway(mover, holderPosition, moverAway, correction))
            return true;

        float fallbackDistance = correction * 0.45f;
        return TryMoveWorkerAway(holder, moverPosition, holderAway, fallbackDistance);
    }

    private AIWorkerAgent GetSeparationMover(AIWorkerAgent a, AIWorkerAgent b)
    {
        int aId = a.GetInstanceID();
        int bId = b.GetInstanceID();
        long key = MakePairKey(aId, bId);
        float now = Time.time;

        if (separationPairs.TryGetValue(key, out SeparationPair pair) && pair.expiresAt > now)
        {
            if (pair.moverId == aId)
                return a;

            if (pair.moverId == bId)
                return b;
        }

        AIWorkerAgent mover = ChooseSeparationMover(a, b);
        separationPairs[key] = new SeparationPair
        {
            moverId = mover.GetInstanceID(),
            expiresAt = now + Mathf.Max(0.1f, separationRoleLifetime)
        };
        return mover;
    }

    private static AIWorkerAgent ChooseSeparationMover(AIWorkerAgent a, AIWorkerAgent b)
    {
        bool aMoving = a.CurrentVelocity.sqrMagnitude > 0.01f;
        bool bMoving = b.CurrentVelocity.sqrMagnitude > 0.01f;
        if (aMoving != bMoving)
            return aMoving ? a : b;

        if (a.IsBlocking != b.IsBlocking)
            return a.IsBlocking ? b : a;

        float aPriority = a.GetRightOfWayPriority();
        float bPriority = b.GetRightOfWayPriority();
        if (Mathf.Abs(aPriority - bPriority) > 0.01f)
            return aPriority < bPriority ? a : b;

        return a.GetInstanceID() > b.GetInstanceID() ? a : b;
    }

    private bool TryMoveWorkerAway(AIWorkerAgent worker, Vector2 otherPosition, Vector2 awayDirection, float distance)
    {
        if (worker == null || distance <= 0.0001f)
            return false;

        Vector2 direction = awayDirection.sqrMagnitude > 0.0001f ? awayDirection.normalized : Vector2.right;
        float step = Mathf.Max(0.01f, distance);
        float[] angleOffsets = { 0f, 25f, -25f, 50f, -50f, 90f, -90f, 140f, -140f };
        float[] stepScales = { 1f, 0.65f, 0.35f };

        for (int stepIndex = 0; stepIndex < stepScales.Length; stepIndex++)
        {
            float scaledStep = step * stepScales[stepIndex];
            for (int angleIndex = 0; angleIndex < angleOffsets.Length; angleIndex++)
            {
                Vector2 candidateDirection = Rotate(direction, angleOffsets[angleIndex] * Mathf.Deg2Rad).normalized;
                Vector2 displacement = candidateDirection * scaledStep;
                if (worker.TryApplyCrowdSeparation(displacement, otherPosition))
                    return true;
            }
        }

        return false;
    }

    public void Register(AIWorkerAgent worker)
    {
        if (worker != null && !workers.Contains(worker))
            workers.Add(worker);
    }

    public void Unregister(AIWorkerAgent worker)
    {
        workers.Remove(worker);
    }

    public float GetPathCost(Vector2Int cell, AIWorkerAgent requester)
    {
        float cost = 0f;
        float now = Time.time;

        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            OfficeGrid2D grid = worker.Grid;
            if (grid == null)
                continue;

            Vector2Int workerCell = grid.WorldToCell(worker.GetPosition());
            int dist = Mathf.Abs(workerCell.x - cell.x) + Mathf.Abs(workerCell.y - cell.y);
            if (dist == 0)
                cost += activeWorkerCellCost;
            else if (dist == 1)
                cost += nearbyWorkerCellCost;
        }

        if (reservations.TryGetValue(cell, out List<Reservation> entries))
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Reservation reservation = entries[i];
                if (reservation.expiresAt <= now)
                {
                    entries.RemoveAt(i);
                    continue;
                }

                if (reservation.owner != requester)
                    cost += reservationCost;
            }
        }

        return cost;
    }

    public void ReserveUpcomingPath(AIWorkerAgent owner, OfficeGrid2D grid, List<Vector2> path, int startIndex, int count)
    {
        if (owner == null || grid == null || path == null)
            return;

        float expiresAt = Time.time + reservationLifetime;
        int end = Mathf.Min(path.Count, startIndex + count);

        for (int i = Mathf.Max(0, startIndex); i < end; i++)
        {
            Vector2Int cell = grid.WorldToCell(path[i]);
            if (!reservations.TryGetValue(cell, out List<Reservation> entries))
            {
                entries = new List<Reservation>();
                reservations.Add(cell, entries);
            }

            bool updated = false;
            for (int j = 0; j < entries.Count; j++)
            {
                if (entries[j].owner != owner)
                    continue;

                entries[j] = new Reservation { owner = owner, expiresAt = expiresAt };
                updated = true;
                break;
            }

            if (!updated)
                entries.Add(new Reservation { owner = owner, expiresAt = expiresAt });
        }
    }

    public bool IsWorkerSpaceFree(Vector2 position, float radius, AIWorkerAgent requester)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            float minDist = GetWorkerClearanceDistance(radius, worker);
            Vector2 delta = worker.GetPosition() - position;
            if (delta.sqrMagnitude < minDist * minDist)
                return false;
        }

        return IsInteractionSpaceFree(position, radius, requester);
    }

    public bool IsWorkerMoveClear(Vector2 from, Vector2 to, float radius, AIWorkerAgent requester)
    {
        const float movingAwayTolerance = 0.0001f;

        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            Vector2 workerPosition = worker.GetPosition();
            float minDist = GetWorkerClearanceDistance(radius, worker);
            float minDistSq = minDist * minDist;
            float fromDistSq = (workerPosition - from).sqrMagnitude;
            float toDistSq = (workerPosition - to).sqrMagnitude;

            if (toDistSq < minDistSq)
            {
                if (toDistSq > fromDistSq + movingAwayTolerance)
                    continue;

                return false;
            }

            float travelDistance = DistancePointToSegment(workerPosition, from, to);
            if (travelDistance >= minDist)
                continue;

            if (fromDistSq < minDistSq && toDistSq > fromDistSq + movingAwayTolerance)
                continue;

            return false;
        }

        return IsInteractionMoveClear(from, to, radius, requester);
    }

    public bool IsWorkerLineClear(Vector2 from, Vector2 to, float radius, AIWorkerAgent requester)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            float minDist = GetWorkerClearanceDistance(radius, worker);
            Vector2 workerPosition = worker.GetPosition();
            if (DistancePointToSegment(workerPosition, from, to) >= minDist)
                continue;

            float fromDist = Vector2.Distance(workerPosition, from);
            float toDist = Vector2.Distance(workerPosition, to);
            if (fromDist < minDist && toDist > fromDist + 0.0001f)
                continue;

            return false;
        }

        return true;
    }

    private static float GetWorkerClearanceDistance(float requesterRadius, AIWorkerAgent worker)
    {
        float workerRadius = worker != null ? worker.avoidanceRadius : requesterRadius;
        float baseDistance = requesterRadius + workerRadius;
        if (worker != null && worker.IsBlocking)
            return baseDistance + Mathf.Max(0.06f, requesterRadius * 0.35f);

        return baseDistance + Mathf.Max(0.02f, requesterRadius * 0.12f);
    }

    public float GetWorkerOverlapPenalty(Vector2 position, float radius, AIWorkerAgent requester)
    {
        float minDist = radius * 2f;
        float penalty = 0f;

        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            float distance = Vector2.Distance(position, worker.GetPosition());
            penalty += Mathf.Max(0f, minDist - distance);
        }

        return penalty;
    }

    public bool CanRecoverWorkerOverlap(Vector2 from, Vector2 to, float radius, AIWorkerAgent requester)
    {
        const float tolerance = 0.0001f;
        float minDist = radius * 2f;
        float fromPenalty = 0f;
        float toPenalty = 0f;
        float fromMaxOverlap = 0f;
        float toMaxOverlap = 0f;
        bool hadOverlap = false;

        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            Vector2 workerPosition = worker.GetPosition();
            float fromOverlap = Mathf.Max(0f, minDist - Vector2.Distance(from, workerPosition));
            float toOverlap = Mathf.Max(0f, minDist - Vector2.Distance(to, workerPosition));

            if (fromOverlap > tolerance)
                hadOverlap = true;
            else if (toOverlap > tolerance)
                return false;

            fromPenalty += fromOverlap;
            toPenalty += toOverlap;
            fromMaxOverlap = Mathf.Max(fromMaxOverlap, fromOverlap);
            toMaxOverlap = Mathf.Max(toMaxOverlap, toOverlap);
        }

        if (!hadOverlap)
            return false;

        if (toPenalty >= fromPenalty - tolerance)
            return false;

        if (toMaxOverlap > fromMaxOverlap + tolerance)
            return false;

        return IsInteractionMoveClear(from, to, radius, requester);
    }

    public bool IsInteractionSpaceFree(Vector2 position, float radius, AIWorkerAgent requester)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (!TryGetBlockingInteraction(worker, requester, out Vector2 furniturePoint, out Vector2 workerPoint, out float blockerRadius))
                continue;

            float minDistance = blockerRadius + radius;
            if (DistancePointToSegment(position, furniturePoint, workerPoint) < minDistance)
                return false;
        }

        return IsActionGroupSpaceFree(position, radius, requester);
    }

    public bool IsInteractionLineClear(Vector2 from, Vector2 to, float radius, AIWorkerAgent requester)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (!TryGetBlockingInteraction(worker, requester, out Vector2 furniturePoint, out Vector2 workerPoint, out float blockerRadius))
                continue;

            float minDistance = blockerRadius + radius;
            if (SegmentToSegmentDistance(from, to, furniturePoint, workerPoint) < minDistance)
                return false;
        }

        return IsActionGroupLineClear(from, to, radius, requester);
    }

    public bool IsInteractionMoveClear(Vector2 from, Vector2 to, float radius, AIWorkerAgent requester)
    {
        const float movingAwayTolerance = 0.0001f;

        foreach (AIWorkerAgent worker in workers)
        {
            if (!TryGetBlockingInteraction(worker, requester, out Vector2 furniturePoint, out Vector2 workerPoint, out float blockerRadius))
                continue;

            float minDistance = blockerRadius + radius;
            float travelDistance = SegmentToSegmentDistance(from, to, furniturePoint, workerPoint);
            if (travelDistance >= minDistance)
                continue;

            float fromDistance = DistancePointToSegment(from, furniturePoint, workerPoint);
            float toDistance = DistancePointToSegment(to, furniturePoint, workerPoint);
            if (fromDistance < minDistance && toDistance > fromDistance + movingAwayTolerance)
                continue;

            return false;
        }

        return IsActionGroupMoveClear(from, to, radius, requester);
    }

    private bool IsActionGroupSpaceFree(Vector2 position, float radius, AIWorkerAgent requester)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (!TryGetBlockingActionGroup(worker, requester, out Vector2 center, out float blockerRadius))
                continue;

            float minDistance = blockerRadius + radius;
            if (Vector2.Distance(position, center) < minDistance)
                return false;
        }

        return true;
    }

    private bool IsActionGroupLineClear(Vector2 from, Vector2 to, float radius, AIWorkerAgent requester)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (!TryGetBlockingActionGroup(worker, requester, out Vector2 center, out float blockerRadius))
                continue;

            float minDistance = blockerRadius + radius;
            if (DistancePointToSegment(center, from, to) < minDistance)
                return false;
        }

        return true;
    }

    private bool IsActionGroupMoveClear(Vector2 from, Vector2 to, float radius, AIWorkerAgent requester)
    {
        const float movingAwayTolerance = 0.0001f;

        foreach (AIWorkerAgent worker in workers)
        {
            if (!TryGetBlockingActionGroup(worker, requester, out Vector2 center, out float blockerRadius))
                continue;

            float minDistance = blockerRadius + radius;
            float travelDistance = DistancePointToSegment(center, from, to);
            if (travelDistance >= minDistance)
                continue;

            float fromDistance = Vector2.Distance(from, center);
            float toDistance = Vector2.Distance(to, center);
            if (fromDistance < minDistance && toDistance > fromDistance + movingAwayTolerance)
                continue;

            return false;
        }

        return true;
    }

    private static bool TryGetBlockingActionGroup(
        AIWorkerAgent worker,
        AIWorkerAgent requester,
        out Vector2 center,
        out float radius)
    {
        center = Vector2.zero;
        radius = 0f;

        if (worker == null)
            return false;

        return worker.TryGetProtectedActionGroup(requester, out center, out radius);
    }

    private static bool TryGetBlockingInteraction(
        AIWorkerAgent worker,
        AIWorkerAgent requester,
        out Vector2 furniturePoint,
        out Vector2 workerPoint,
        out float radius)
    {
        furniturePoint = Vector2.zero;
        workerPoint = Vector2.zero;
        radius = 0f;

        if (worker == null || worker == requester)
            return false;

        return worker.TryGetProtectedInteractionSpace(out furniturePoint, out workerPoint, out radius);
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

    private static float SegmentToSegmentDistance(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        if (SegmentsIntersect(a0, a1, b0, b1))
            return 0f;

        return Mathf.Min(
            Mathf.Min(DistancePointToSegment(a0, b0, b1), DistancePointToSegment(a1, b0, b1)),
            Mathf.Min(DistancePointToSegment(b0, a0, a1), DistancePointToSegment(b1, a0, a1)));
    }

    private static bool SegmentsIntersect(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        const float epsilon = 0.0001f;
        Vector2 r = a1 - a0;
        Vector2 s = b1 - b0;
        float denominator = Cross(r, s);
        float numerator = Cross(b0 - a0, r);

        if (Mathf.Abs(denominator) <= epsilon)
            return Mathf.Abs(numerator) <= epsilon &&
                   (DistancePointToSegment(a0, b0, b1) <= epsilon ||
                    DistancePointToSegment(a1, b0, b1) <= epsilon ||
                    DistancePointToSegment(b0, a0, a1) <= epsilon ||
                    DistancePointToSegment(b1, a0, a1) <= epsilon);

        float t = Cross(b0 - a0, s) / denominator;
        float u = Cross(b0 - a0, r) / denominator;
        return t >= -epsilon && t <= 1f + epsilon && u >= -epsilon && u <= 1f + epsilon;
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static long MakePairKey(int a, int b)
    {
        uint low = (uint)Mathf.Min(a, b);
        uint high = (uint)Mathf.Max(a, b);
        return ((long)low << 32) | high;
    }

    private static Vector2 DeterministicPairDirection(AIWorkerAgent a, AIWorkerAgent b)
    {
        int aId = a.GetInstanceID();
        int bId = b.GetInstanceID();
        int lowId = Mathf.Min(aId, bId);
        int highId = Mathf.Max(aId, bId);
        uint hash = unchecked((uint)(lowId * 486187739) ^ (uint)(highId * 16777619));
        float angle = (hash % 360u) * Mathf.Deg2Rad;
        Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        return aId < bId ? direction : -direction;
    }

    private static Vector2 Rotate(Vector2 vector, float radians)
    {
        float sin = Mathf.Sin(radians);
        float cos = Mathf.Cos(radians);
        return new Vector2(
            vector.x * cos - vector.y * sin,
            vector.x * sin + vector.y * cos);
    }

    public void GetNearbyWorkers(Vector2 position, float range, AIWorkerAgent requester, List<AIWorkerAgent> result)
    {
        result.Clear();
        float rangeSq = range * range;

        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            if (((Vector2)worker.GetPosition() - position).sqrMagnitude <= rangeSq)
                result.Add(worker);
        }
    }

}
