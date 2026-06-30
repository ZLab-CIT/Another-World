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

    private bool[,] walkable;

    public bool Ready => walkable != null;

    private void Awake()
    {
        Rebuild();
    }

    public void Rebuild()
    {
        walkable = new bool[width, height];

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

    public IEnumerable<Vector2Int> GetNeighbors(Vector2Int cell)
    {
        Vector2Int[] directions =
        {
            Vector2Int.up,
            Vector2Int.down,
            Vector2Int.left,
            Vector2Int.right
        };

        foreach (Vector2Int direction in directions)
        {
            Vector2Int neighbor = cell + direction;

            if (IsWalkable(neighbor))
                yield return neighbor;
        }
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