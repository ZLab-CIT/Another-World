using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(AIWorkerAgent))]
[RequireComponent(typeof(OfficeWorkerMotor2D))]
public class AgentNavigationController : MonoBehaviour
{
    public enum TickResult
    {
        Moving,
        Arrived,
        Failed
    }

    private AIWorkerAgent owner;
    private OfficeWorkerMotor2D motor;
    private List<Vector2> currentPath;
    private int pathIndex;

    private void Awake()
    {
        owner = GetComponent<AIWorkerAgent>();
        motor = GetComponent<OfficeWorkerMotor2D>();
    }

    public bool Plan(OfficeActionPoint action)
    {
        if (action == null || owner.Grid == null)
            return false;

        Vector2 targetPosition = action.GetTargetPosition(owner);
        currentPath = OfficePathfinder2D.FindPath(
            owner.Grid,
            owner.GetPosition(),
            targetPosition,
            owner.navigationRadius);
        if (currentPath == null || currentPath.Count == 0)
            return false;

        pathIndex = Mathf.Min(1, currentPath.Count - 1);
        return true;
    }

    public TickResult Tick()
    {
        if (currentPath == null || currentPath.Count == 0)
            return TickResult.Failed;

        AdvancePathIndex();
        if (pathIndex >= currentPath.Count)
            return TickResult.Arrived;

        Vector2 target = CurrentPathTarget();
        bool moved = motor.MoveToward(
            owner.Grid,
            target,
            owner.navigationRadius,
            owner.speed * owner.EffectiveSpeedMultiplier,
            Time.deltaTime);

        if (!moved)
            return TickResult.Failed;

        return TickResult.Moving;
    }

    public void Clear()
    {
        currentPath = null;
        pathIndex = 0;
        motor.Stop();
    }

    private Vector2 CurrentPathTarget()
    {
        return currentPath[pathIndex];
    }

    private void AdvancePathIndex()
    {
        while (pathIndex < currentPath.Count)
        {
            bool isLast = pathIndex == currentPath.Count - 1;
            float threshold = isLast ? owner.slotArriveDistance : owner.arriveDistance;
            if (Vector2.Distance(owner.GetPosition(), CurrentPathTarget()) > threshold)
                break;

            pathIndex++;
        }
    }
}
