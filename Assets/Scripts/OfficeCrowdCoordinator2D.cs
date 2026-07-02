using System.Collections.Generic;
using UnityEngine;

public class OfficeCrowdCoordinator2D : MonoBehaviour
{
    private struct Reservation
    {
        public AIWorkerAgent owner;
        public float expiresAt;
    }

    public static OfficeCrowdCoordinator2D Instance { get; private set; }

    [Header("Crowd Costs")]
    public float activeWorkerCellCost = 6f;
    public float nearbyWorkerCellCost = 2f;
    public float reservationCost = 4f;
    public float reservationLifetime = 1.2f;

    private readonly List<AIWorkerAgent> workers = new List<AIWorkerAgent>();
    private readonly Dictionary<Vector2Int, List<Reservation>> reservations = new Dictionary<Vector2Int, List<Reservation>>();

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
        float minDist = radius * 2f;
        float minDistSq = minDist * minDist;

        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            Vector2 delta = worker.GetPosition() - position;
            if (delta.sqrMagnitude < minDistSq)
                return false;
        }

        return true;
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
