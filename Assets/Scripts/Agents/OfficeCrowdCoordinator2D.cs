using System.Collections.Generic;
using UnityEngine;

// Keeps track of workers for social and event systems. Workers do not block one another.
public class OfficeCrowdCoordinator2D : MonoBehaviour
{
    public static OfficeCrowdCoordinator2D Instance { get; private set; }

    private readonly List<AIWorkerAgent> workers = new();
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
    }

    public void GetNearbyWorkers(Vector2 position, float range, AIWorkerAgent requester, List<AIWorkerAgent> result)
    {
        result.Clear();
        float rangeSquared = range * range;
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == requester)
                continue;

            if (((Vector2)worker.GetPosition() - position).sqrMagnitude <= rangeSquared)
                result.Add(worker);
        }
    }
}
