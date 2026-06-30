using System.Collections.Generic;
using UnityEngine;

public static class AStarPathfinder2D
{
    public static List<Vector2> FindPath(
        OfficeGrid2D grid,
        Vector2 startWorld,
        Vector2 targetWorld
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
                return ReconstructPath(grid, cameFrom, current);

            openSet.Remove(current);
            closedSet.Add(current);

            foreach (Vector2Int neighbor in grid.GetNeighbors(current))
            {
                if (closedSet.Contains(neighbor))
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
}