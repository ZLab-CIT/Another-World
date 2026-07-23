using System.Collections.Generic;
using UnityEngine;

// Keeps track of workers for social and event systems
public class OfficeCrowdCoordinator2D : MonoBehaviour
{
    public static OfficeCrowdCoordinator2D Instance { get; private set; }

    private readonly List<AIWorkerAgent> workers = new();
    private readonly Dictionary<AIWorkerAgent, Vector2> destinationReservations = new();
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

        return new GameObject(nameof(OfficeCrowdCoordinator2D)).AddComponent<OfficeCrowdCoordinator2D>();
    }

    public void Register(AIWorkerAgent worker)
    {
        if (worker != null && !workers.Contains(worker))
            workers.Add(worker);
    }

    public void Unregister(AIWorkerAgent worker)
    {
        workers.Remove(worker);
        destinationReservations.Remove(worker);
    }

    public bool IsPositionAvailable(Vector2 position, AIWorkerAgent requester,
        float minimumDistance, AIWorkerAgent ignoredWorker = null)
    {
        float distanceSquared = minimumDistance * minimumDistance;
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester || worker == ignoredWorker)
                continue;
            if ((worker.GetPosition() - position).sqrMagnitude < distanceSquared)
                return false;
        }

        foreach (KeyValuePair<AIWorkerAgent, Vector2> reservation in destinationReservations)
        {
            if (reservation.Key == null || reservation.Key == requester
                || reservation.Key == ignoredWorker)
                continue;
            if ((reservation.Value - position).sqrMagnitude < distanceSquared)
                return false;
        }
        return true;
    }

    public void ReserveDestination(AIWorkerAgent worker, Vector2 destination)
    {
        if (worker != null)
            destinationReservations[worker] = destination;
    }

    public void ClearDestination(AIWorkerAgent worker)
    {
        if (worker != null)
            destinationReservations.Remove(worker);
    }

    public void GetNearbyWorkers(Vector2 position, float range, AIWorkerAgent requester, List<AIWorkerAgent> result)
    {
        result.Clear();
        float rangeSquared = range * range;
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            if ((worker.GetPosition() - position).sqrMagnitude <= rangeSquared)
                result.Add(worker);
        }
    }
}
