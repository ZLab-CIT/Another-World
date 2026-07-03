using System.Collections.Generic;
using UnityEngine;

public class OfficeGrid2D : MonoBehaviour
{
    [Header("Grid Size")]
    public int width = 32;
    public int height = 20;
    public float cellSize = 0.5f;

    [Header("Grid Origin")]
    public Vector2 origin = new Vector2(-8f, -5f);

    [Header("Obstacle Detection")]
    public LayerMask obstacleMask;

    [Header("Agent Clearance")]
    public float agentClearanceRadius = 0.35f;

    [Range(0.1f, 1f)]
    public float obstacleCheckSize = 0.8f;

    [Header("Routing Costs")]
    [Tooltip("Extra path cost for cells that are only barely wide enough for a worker.")]
    public float lowClearancePenalty = 4f;
    [Tooltip("Maximum distance sampled when estimating corridor width.")]
    public float clearanceProbeDistance = 2f;

    private bool[,] walkable;
    private float[,] clearance;

    public bool Ready => walkable != null;

    private void Awake()
    {
        Rebuild();
    }

    public void Rebuild()
    {
        walkable = new bool[width, height];
        clearance = new float[width, height];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector2 worldPosition = CellToWorld(new Vector2Int(x, y));

                Collider2D obstacle = Physics2D.OverlapCircle(
                    worldPosition,
                    agentClearanceRadius,
                    obstacleMask
                );

                walkable[x, y] = obstacle == null;
                clearance[x, y] = walkable[x, y]
                    ? EstimateClearance(worldPosition)
                    : 0f;
            }
        }
    }

    public Vector2Int WorldToCell(Vector2 worldPosition)
    {
        int x = Mathf.FloorToInt((worldPosition.x - origin.x) / cellSize);
        int y = Mathf.FloorToInt((worldPosition.y - origin.y) / cellSize);

        return new Vector2Int(x, y);
    }

    public Vector2 CellToWorld(Vector2Int cell)
    {
        return origin + new Vector2(
            (cell.x + 0.5f) * cellSize,
            (cell.y + 0.5f) * cellSize
        );
    }

    public bool InBounds(Vector2Int cell)
    {
        return cell.x >= 0 &&
               cell.y >= 0 &&
               cell.x < width &&
               cell.y < height;
    }

    public bool IsWalkable(Vector2Int cell)
    {
        if (!InBounds(cell))
            return false;

        return walkable[cell.x, cell.y];
    }

    public float GetClearance(Vector2Int cell)
    {
        if (!InBounds(cell) || clearance == null)
            return 0f;

        return clearance[cell.x, cell.y];
    }

    public float GetStaticCost(Vector2Int cell, float workerRadius)
    {
        if (!IsWalkable(cell))
            return float.PositiveInfinity;

        float cellClearance = GetClearance(cell);
        float desiredClearance = workerRadius * 2.6f;
        if (cellClearance >= desiredClearance)
            return 0f;

        float t = Mathf.Clamp01((desiredClearance - cellClearance) / Mathf.Max(0.01f, desiredClearance));
        return t * lowClearancePenalty;
    }

    public bool IsBodyPositionClear(Vector2 worldPosition, float radius)
    {
        Vector2Int cell = WorldToCell(worldPosition);
        if (!IsWalkable(cell))
            return false;

        return Physics2D.OverlapCircle(worldPosition, radius, obstacleMask) == null;
    }
    public bool IsBodyPhysicallyClear(Vector2 worldPosition, float radius)
    {
        if (!InBounds(WorldToCell(worldPosition)))
            return false;

        return Physics2D.OverlapCircle(worldPosition, radius, obstacleMask) == null;
    }

    public bool CanMoveBody(Vector2 from, Vector2 to, float radius)
    {
        Vector2 delta = to - from;
        float dist = delta.magnitude;

        if (dist <= 0.0001f)
            return IsBodyPositionClear(to, radius);

        RaycastHit2D hit = Physics2D.CircleCast(from, radius, delta / dist, dist, obstacleMask);
        return hit.collider == null && IsBodyPositionClear(to, radius);
    }
    public bool CanMoveBodyPhysically(Vector2 from, Vector2 to, float radius)
    {
        Vector2 delta = to - from;
        float dist = delta.magnitude;

        if (dist <= 0.0001f)
            return IsBodyPhysicallyClear(to, radius);

        RaycastHit2D hit = Physics2D.CircleCast(from, radius, delta / dist, dist, obstacleMask);
        return hit.collider == null && IsBodyPhysicallyClear(to, radius);
    }

    public bool CanRecoverBody(Vector2 from, Vector2 to, float radius)
    {
        if (!InBounds(WorldToCell(to)))
            return false;

        if (CanMoveBodyPhysically(from, to, radius))
            return true;

        float fromPenalty = GetBodyBlockPenalty(from, radius);
        if (fromPenalty <= 0.0001f)
            return false;

        float toPenalty = GetBodyBlockPenalty(to, radius);
        return toPenalty < fromPenalty - 0.0001f;
    }

    public bool HasBodyLineOfSight(Vector2 from, Vector2 to, float radius)
    {
        Vector2 delta = to - from;
        float dist = delta.magnitude;
        if (dist <= 0.0001f)
            return IsBodyPositionClear(to, radius);

        RaycastHit2D hit = Physics2D.CircleCast(from, radius, delta / dist, dist, obstacleMask);
        if (hit.collider != null)
            return false;

        int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (cellSize * 0.5f)));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(from, to, i / (float)steps);
            if (!IsBodyPositionClear(p, radius))
                return false;
        }

        return true;
    }

    public bool TryFindNearestWalkable(Vector2 worldPosition, float radius, out Vector2 result)
    {
        Vector2Int start = WorldToCell(worldPosition);
        if (IsBodyPositionClear(worldPosition, radius))
        {
            result = worldPosition;
            return true;
        }

        int maxRing = Mathf.Max(width, height);
        for (int ring = 0; ring <= maxRing; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    if (Mathf.Abs(dx) != ring && Mathf.Abs(dy) != ring)
                        continue;

                    Vector2Int cell = new Vector2Int(start.x + dx, start.y + dy);
                    if (!IsWalkable(cell))
                        continue;

                    Vector2 candidate = CellToWorld(cell);
                    if (IsBodyPositionClear(candidate, radius))
                    {
                        result = candidate;
                        return true;
                    }
                }
            }
        }

        result = worldPosition;
        return false;
    }

    public IEnumerable<Vector2Int> GetNeighbors(Vector2Int cell)
    {
        Vector2Int[] directions =
        {
            Vector2Int.up,
            Vector2Int.down,
            Vector2Int.left,
            Vector2Int.right,
            new Vector2Int(1, 1),
            new Vector2Int(1, -1),
            new Vector2Int(-1, 1),
            new Vector2Int(-1, -1)
        };

        foreach (Vector2Int direction in directions)
        {
            Vector2Int neighbor = cell + direction;

            if (Mathf.Abs(direction.x) == 1 && Mathf.Abs(direction.y) == 1)
            {
                if (!IsWalkable(cell + new Vector2Int(direction.x, 0)) ||
                    !IsWalkable(cell + new Vector2Int(0, direction.y)))
                    continue;
            }

            if (IsWalkable(neighbor))
                yield return neighbor;
        }
    }

    private float EstimateClearance(Vector2 worldPosition)
    {
        if (obstacleMask.value == 0)
            return clearanceProbeDistance;

        float best = clearanceProbeDistance;
        const int rays = 16;

        for (int i = 0; i < rays; i++)
        {
            float angle = (i / (float)rays) * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            RaycastHit2D hit = Physics2D.Raycast(worldPosition, dir, clearanceProbeDistance, obstacleMask);
            if (hit.collider != null)
                best = Mathf.Min(best, hit.distance);
        }

        return best;
    }
    private float GetBodyBlockPenalty(Vector2 worldPosition, float radius)
    {
        if (!InBounds(WorldToCell(worldPosition)))
            return float.PositiveInfinity;

        float probeRadius = Mathf.Max(radius, agentClearanceRadius);
        Collider2D[] hits = Physics2D.OverlapCircleAll(worldPosition, probeRadius, obstacleMask);
        float penalty = 0f;

        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D hit = hits[i];
            if (hit == null || !hit.enabled)
                continue;

            float distance = Vector2.Distance(worldPosition, hit.ClosestPoint(worldPosition));
            float bodyOverlap = Mathf.Max(0f, radius - distance);
            float clearanceShortfall = Mathf.Max(0f, agentClearanceRadius - distance) * 0.35f;
            penalty += Mathf.Max(bodyOverlap, clearanceShortfall);
        }

        return penalty;
    }

    private void OnDrawGizmosSelected()
    {
        if (walkable == null)
            return;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector2 worldPosition = CellToWorld(new Vector2Int(x, y));
                Gizmos.DrawWireCube(worldPosition, Vector3.one * cellSize * 0.9f);
            }
        }
    }
}
