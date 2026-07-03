using System.Collections.Generic;
using UnityEngine;

public static class OfficePathfinder2D
{
    private sealed class Node
    {
        public Vector2Int cell;
        public float g;
        public float f;
    }

    public static List<Vector2> FindPath(
        OfficeGrid2D grid,
        Vector2 startWorld,
        Vector2 targetWorld,
        AIWorkerAgent requester,
        float workerRadius)
    {
        if (grid == null || !grid.Ready)
            return null;

        if (!grid.TryFindNearestWalkable(startWorld, workerRadius, out Vector2 safeStart))
            return null;

        if (!grid.TryFindNearestWalkable(targetWorld, workerRadius, out Vector2 safeTarget))
            return null;

        Vector2Int start = grid.WorldToCell(safeStart);
        Vector2Int goal = grid.WorldToCell(safeTarget);
        if (!grid.InBounds(start) || !grid.InBounds(goal))
            return null;

        List<Node> open = new List<Node>();
        HashSet<Vector2Int> closed = new HashSet<Vector2Int>();
        Dictionary<Vector2Int, Vector2Int> cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        Dictionary<Vector2Int, float> gScore = new Dictionary<Vector2Int, float>();

        gScore[start] = 0f;
        open.Add(new Node { cell = start, g = 0f, f = Heuristic(start, goal) });

        while (open.Count > 0)
        {
            int bestIndex = GetBestOpenIndex(open);
            Node current = open[bestIndex];
            open.RemoveAt(bestIndex);

            if (current.cell == goal)
                return Smooth(grid, BuildWorldPath(grid, cameFrom, current.cell, safeStart, safeTarget), workerRadius, requester);

            if (!closed.Add(current.cell))
                continue;

            foreach (Vector2Int neighbor in grid.GetNeighbors(current.cell))
            {
                if (closed.Contains(neighbor))
                    continue;

                Vector2 neighborWorld = grid.CellToWorld(neighbor);
                if (!grid.IsBodyPositionClear(neighborWorld, workerRadius) && neighbor != goal)
                    continue;

                OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
                if (crowd != null && !crowd.IsWorkerSpaceFree(neighborWorld, workerRadius, requester))
                    continue;

                float moveCost = Vector2Int.Distance(current.cell, neighbor);
                float turnCost = GetTurnCost(cameFrom, current.cell, neighbor);
                float staticCost = grid.GetStaticCost(neighbor, workerRadius);
                float crowdCost = crowd != null
                    ? crowd.GetPathCost(neighbor, requester)
                    : 0f;

                float tentative = current.g + moveCost + turnCost + staticCost + crowdCost;
                if (tentative >= GetScore(gScore, neighbor))
                    continue;

                cameFrom[neighbor] = current.cell;
                gScore[neighbor] = tentative;
                float f = tentative + Heuristic(neighbor, goal);
                AddOrUpdate(open, neighbor, tentative, f);
            }
        }

        return null;
    }

    private static int GetBestOpenIndex(List<Node> open)
    {
        int best = 0;
        float bestF = open[0].f;
        for (int i = 1; i < open.Count; i++)
        {
            if (open[i].f < bestF)
            {
                best = i;
                bestF = open[i].f;
            }
        }

        return best;
    }

    private static void AddOrUpdate(List<Node> open, Vector2Int cell, float g, float f)
    {
        for (int i = 0; i < open.Count; i++)
        {
            if (open[i].cell != cell)
                continue;

            open[i].g = g;
            open[i].f = f;
            return;
        }

        open.Add(new Node { cell = cell, g = g, f = f });
    }

    private static float GetScore(Dictionary<Vector2Int, float> scores, Vector2Int cell)
    {
        return scores.TryGetValue(cell, out float score) ? score : float.PositiveInfinity;
    }

    private static float Heuristic(Vector2Int a, Vector2Int b)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dy = Mathf.Abs(a.y - b.y);
        return Mathf.Max(dx, dy) + 0.4142f * Mathf.Min(dx, dy);
    }

    private static float GetTurnCost(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int current, Vector2Int next)
    {
        if (!cameFrom.TryGetValue(current, out Vector2Int previous))
            return 0f;

        Vector2Int a = current - previous;
        Vector2Int b = next - current;
        return a == b ? 0f : 0.12f;
    }

    private static List<Vector2> BuildWorldPath(
        OfficeGrid2D grid,
        Dictionary<Vector2Int, Vector2Int> cameFrom,
        Vector2Int current,
        Vector2 startWorld,
        Vector2 targetWorld)
    {
        List<Vector2Int> cells = new List<Vector2Int> { current };
        while (cameFrom.TryGetValue(current, out Vector2Int previous))
        {
            current = previous;
            cells.Add(current);
        }

        cells.Reverse();

        List<Vector2> path = new List<Vector2>(cells.Count);
        for (int i = 0; i < cells.Count; i++)
            path.Add(grid.CellToWorld(cells[i]));

        if (path.Count > 0)
        {
            path[0] = startWorld;
            path[path.Count - 1] = targetWorld;
        }

        return path;
    }

    private static List<Vector2> Smooth(OfficeGrid2D grid, List<Vector2> path, float workerRadius, AIWorkerAgent requester)
    {
        if (path == null || path.Count <= 2)
            return path;

        List<Vector2> smoothed = new List<Vector2> { path[0] };
        int anchor = 0;

        while (anchor < path.Count - 1)
        {
            int next = anchor + 1;
            for (int i = path.Count - 1; i > anchor + 1; i--)
            {
                if (HasClearLine(grid, path[anchor], path[i], workerRadius, requester))
                {
                    next = i;
                    break;
                }
            }

            smoothed.Add(path[next]);
            anchor = next;
        }

        return smoothed;
    }

    private static bool HasClearLine(
        OfficeGrid2D grid,
        Vector2 from,
        Vector2 to,
        float workerRadius,
        AIWorkerAgent requester)
    {
        if (!grid.HasBodyLineOfSight(from, to, workerRadius))
            return false;

        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        return crowd == null ||
               (crowd.IsWorkerLineClear(from, to, workerRadius, requester) &&
                crowd.IsInteractionLineClear(from, to, workerRadius, requester));
    }
}
