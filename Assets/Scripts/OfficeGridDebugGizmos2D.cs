using UnityEngine;

/// <summary>
/// Optional helper. Attach this to the same object as OfficeGrid2D.
/// It draws walkable/blocked cells and the clearance circle used when building the grid.
/// Disable it for the final 24/7 build.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(OfficeGrid2D))]
public class OfficeGridDebugGizmos2D : MonoBehaviour
{
    public OfficeGrid2D grid;
    public bool drawCells = true;
    public bool drawClearanceCircles = false;
    public bool selectedOnly = false;

    private void Reset()
    {
        grid = GetComponent<OfficeGrid2D>();
    }

    private void OnValidate()
    {
        if (grid == null)
            grid = GetComponent<OfficeGrid2D>();
    }

    private void OnDrawGizmos()
    {
        if (selectedOnly)
            return;

        Draw();
    }

    private void OnDrawGizmosSelected()
    {
        Draw();
    }

    private void Draw()
    {
        if (grid == null)
            grid = GetComponent<OfficeGrid2D>();

        if (grid == null || !grid.Ready)
            return;

        for (int x = 0; x < grid.width; x++)
        {
            for (int y = 0; y < grid.height; y++)
            {
                Vector2Int cell = new Vector2Int(x, y);
                Vector2 world = grid.CellToWorld(cell);
                bool walkable = grid.IsWalkable(cell);

                if (drawCells)
                {
                    Gizmos.color = walkable
                        ? new Color(0f, 1f, 0f, 0.25f)
                        : new Color(1f, 0f, 0f, 0.45f);

                    Gizmos.DrawWireCube(world, Vector3.one * grid.cellSize * 0.9f);
                }

                if (drawClearanceCircles)
                {
                    Gizmos.color = walkable
                        ? new Color(0f, 1f, 1f, 0.15f)
                        : new Color(1f, 0f, 0f, 0.25f);

                    Gizmos.DrawWireSphere(world, grid.agentClearanceRadius);
                }
            }
        }
    }
}
