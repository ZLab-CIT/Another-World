using System.Collections.Generic;
using UnityEngine;

public static class VendingTargetResolver
{
    public static List<AIWorkerAgent> Resolve(VendingEventSO evt, HatCatalogSO hatCatalog)
    {
        List<AIWorkerAgent> targets = new();
        if (evt is VendingHatEventSO)
            return ResolveHatTarget(hatCatalog);

        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null || crowd.Workers.Count == 0)
            return targets;

        switch (evt.scope)
        {
            case VendingEventScope.AllAgents:
                foreach (AIWorkerAgent worker in crowd.Workers)
                    if (worker != null)
                        targets.Add(worker);
                break;
            case VendingEventScope.NearbyAgents:
                AIWorkerAgent center = PickRandomWorker(crowd);
                if (center != null)
                {
                    crowd.GetNearbyWorkers(center.GetPosition(), evt.nearbyRadius, center, targets);
                    targets.Add(center);
                }
                break;
            default:
                AIWorkerAgent single = PickRandomWorker(crowd);
                if (single != null)
                    targets.Add(single);
                break;
        }
        return targets;
    }

    private static List<AIWorkerAgent> ResolveHatTarget(HatCatalogSO hatCatalog)
    {
        List<AIWorkerAgent> targets = new();
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (hatCatalog == null || crowd == null)
            return targets;

        List<AIWorkerAgent> qualified = new();
        foreach (AIWorkerAgent worker in crowd.Workers)
            if (worker != null && hatCatalog.GetPool(worker.AgentType) != null)
                qualified.Add(worker);
        if (qualified.Count > 0)
            targets.Add(qualified[Random.Range(0, qualified.Count)]);
        return targets;
    }

    private static AIWorkerAgent PickRandomWorker(OfficeCrowdCoordinator2D crowd)
    {
        int count = crowd.Workers.Count;
        for (int attempt = 0; attempt < count; attempt++)
        {
            AIWorkerAgent worker = crowd.Workers[Random.Range(0, count)];
            if (worker != null)
                return worker;
        }
        return null;
    }
}
