using System.Collections.Generic;
using UnityEngine;

public static class AStarPathfinder2D
{
    public static List<Vector2> FindPath(
        OfficeGrid2D grid,
        Vector2 startWorld,
        Vector2 targetWorld,
        HashSet<Vector2Int> blockedCells = null,
        float clearance = 0f
    )
    {
        if (grid == null || !grid.Ready)
            return null;

        Vector2Int start = grid.WorldToCell(startWorld);
        Vector2Int goal = grid.WorldToCell(targetWorld);

        if (!grid.InBounds(start) || !grid.InBounds(goal))
            return null;

        if (!grid.IsWalkable(goal))
            return null;

        List<Vector2Int> openSet = new List<Vector2Int> { start };
        HashSet<Vector2Int> closedSet = new HashSet<Vector2Int>();

        Dictionary<Vector2Int, Vector2Int> cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        Dictionary<Vector2Int, int> gScore = new Dictionary<Vector2Int, int>();

        gScore[start] = 0;

        while (openSet.Count > 0)
        {
            Vector2Int current = GetLowestFScore(openSet, gScore, goal);

            if (current == goal)
                return SmoothPath(ReconstructPath(grid, cameFrom, current), startWorld, grid, blockedCells, clearance);

            openSet.Remove(current);
            closedSet.Add(current);

            foreach (Vector2Int neighbor in grid.GetNeighbors(current))
            {
                if (closedSet.Contains(neighbor))
                    continue;

                // Skip cells occupied by stationary agents, unless that cell is
                // the goal (an agent must still be able to target its own desk).
                if (blockedCells != null && blockedCells.Contains(neighbor) && neighbor != goal)
                    continue;

                int tentativeGScore = GetGScore(gScore, current) + 1;

                if (!openSet.Contains(neighbor))
                {
                    openSet.Add(neighbor);
                }
                else if (tentativeGScore >= GetGScore(gScore, neighbor))
                {
                    continue;
                }

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeGScore;
            }
        }

        return null;
    }

    private static Vector2Int GetLowestFScore(
        List<Vector2Int> openSet,
        Dictionary<Vector2Int, int> gScore,
        Vector2Int goal
    )
    {
        Vector2Int best = openSet[0];
        int bestScore = GetGScore(gScore, best) + Heuristic(best, goal);

        for (int i = 1; i < openSet.Count; i++)
        {
            Vector2Int candidate = openSet[i];
            int candidateScore = GetGScore(gScore, candidate) + Heuristic(candidate, goal);

            if (candidateScore < bestScore)
            {
                best = candidate;
                bestScore = candidateScore;
            }
        }

        return best;
    }

    private static int GetGScore(Dictionary<Vector2Int, int> gScore, Vector2Int cell)
    {
        return gScore.TryGetValue(cell, out int value) ? value : int.MaxValue / 4;
    }

    private static int Heuristic(Vector2Int a, Vector2Int b)
    {
        return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    }

    private static List<Vector2> ReconstructPath(
        OfficeGrid2D grid,
        Dictionary<Vector2Int, Vector2Int> cameFrom,
        Vector2Int current
    )
    {
        List<Vector2Int> cells = new List<Vector2Int> { current };

        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            cells.Add(current);
        }

        cells.Reverse();

        List<Vector2> worldPath = new List<Vector2>();

        foreach (Vector2Int cell in cells)
        {
            worldPath.Add(grid.CellToWorld(cell));
        }

        return worldPath;
    }

    private static List<Vector2> SmoothPath(
        List<Vector2> path,
        Vector2 startWorld,
        OfficeGrid2D grid,
        HashSet<Vector2Int> blockedCells,
        float clearance
    )
    {
        if (path == null || path.Count == 0)
            return path;

        // Start from the agent's actual position instead of the cell center.
        path[0] = startWorld;

        if (path.Count <= 2)
            return path;

        // String-pulling: collapse any run of waypoints that has clear
        // line-of-sight into a single straight segment. Turns the 4-connected
        // staircase into straight diagonals where the space is open.
        List<Vector2> smoothed = new List<Vector2> { path[0] };

        int anchor = 0;
        while (anchor < path.Count - 1)
        {
            int next = anchor + 1;

            for (int j = path.Count - 1; j > anchor + 1; j--)
            {
                if (HasLineOfSight(path[anchor], path[j], grid, blockedCells, clearance))
                {
                    next = j;
                    break;
                }
            }

            smoothed.Add(path[next]);
            anchor = next;
        }

        return smoothed;
    }

    private static bool HasLineOfSight(
        Vector2 a,
        Vector2 b,
        OfficeGrid2D grid,
        HashSet<Vector2Int> blockedCells,
        float clearance
    )
    {
        Vector2Int cellA = grid.WorldToCell(a);
        Vector2Int cellB = grid.WorldToCell(b);

        float dist = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (grid.cellSize * 0.5f)));

        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            Vector2 p = Vector2.Lerp(a, b, t);
            Vector2Int cell = grid.WorldToCell(p);

            // Endpoints are existing path nodes (or the goal) and are allowed.
            if (cell == cellA || cell == cellB)
                continue;

            if (!grid.IsWalkable(cell))
                return false;

            // Preserve dynamic avoidance: don't cut through a stationed agent.
            if (blockedCells != null && blockedCells.Contains(cell))
                return false;

            // Radius-aware: a straight segment between two walkable cell
            // centers can still graze a desk corner closer than the agent's
            // body. Reject the cut if any sample is within the agent's
            // clearance of an actual obstacle collider.
            if (clearance > 0f && Physics2D.OverlapCircle(p, clearance, grid.obstacleMask) != null)
                return false;
        }

        return true;
    }
}