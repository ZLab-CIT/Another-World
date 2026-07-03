using System.Collections.Generic;
using UnityEngine;

public enum OfficeEncounterRole
{
    None,
    MutualRightPass,
    Passer,
    Yielder
}

public struct OfficeEncounterDecision
{
    public OfficeEncounterRole role;
    public Vector2 sideDirection;
    public float speedMultiplier;
}

public class OfficeEncounterCoordinator2D : MonoBehaviour
{
    private struct Encounter
    {
        public int lowId;
        public int highId;
        public int passerId;
        public OfficeEncounterRole lowRole;
        public OfficeEncounterRole highRole;
        public Vector2 lowSideDirection;
        public Vector2 highSideDirection;
        public float expiresAt;
    }

    public static OfficeEncounterCoordinator2D Instance { get; private set; }

    [Header("Encounter Timing")]
    public float encounterLifetime = 1f;

    [Header("Passing")]
    public float mutualPassSpeed = 0.95f;
    public float passerSpeed = 0.9f;
    public float yielderSpeed = 0.25f;

    private readonly Dictionary<long, Encounter> encounters = new Dictionary<long, Encounter>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public static OfficeEncounterCoordinator2D Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new GameObject(nameof(OfficeEncounterCoordinator2D));
        return go.AddComponent<OfficeEncounterCoordinator2D>();
    }

    public OfficeEncounterDecision GetEncounterDecision(
        AIWorkerAgent self,
        AIWorkerAgent other,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 desiredDir,
        float radius,
        float sideStepDistance)
    {
        Vector2 right = new Vector2(desiredDir.y, -desiredDir.x);
        OfficeEncounterDecision decision = new OfficeEncounterDecision
        {
            role = OfficeEncounterRole.None,
            sideDirection = right,
            speedMultiplier = 1f
        };

        if (self == null || other == null || grid == null || crowd == null || desiredDir.sqrMagnitude <= 0.0001f)
            return decision;

        int selfId = self.GetInstanceID();
        int otherId = other.GetInstanceID();
        long key = MakeKey(selfId, otherId);
        float now = Time.time;

        if (!encounters.TryGetValue(key, out Encounter encounter) || encounter.expiresAt <= now)
        {
            encounter = CreateEncounter(self, other, grid, crowd, desiredDir.normalized, radius, sideStepDistance, now);
            encounters[key] = encounter;
        }
        else
        {
            encounter.expiresAt = now + encounterLifetime;
            encounters[key] = encounter;
        }

        OfficeEncounterRole role = selfId == encounter.lowId ? encounter.lowRole : encounter.highRole;
        Vector2 sideDirection = selfId == encounter.lowId ? encounter.lowSideDirection : encounter.highSideDirection;
        decision.role = role;
        decision.sideDirection = sideDirection.sqrMagnitude > 0.0001f ? sideDirection.normalized : right;
        decision.speedMultiplier = GetSpeedMultiplier(role);
        return decision;
    }

    private Encounter CreateEncounter(
        AIWorkerAgent a,
        AIWorkerAgent b,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 aDesiredDir,
        float radius,
        float sideStepDistance,
        float now)
    {
        int aId = a.GetInstanceID();
        int bId = b.GetInstanceID();
        int lowId = Mathf.Min(aId, bId);
        int highId = Mathf.Max(aId, bId);

        Vector2 bDir = b.CurrentVelocity.sqrMagnitude > 0.0001f
            ? b.CurrentVelocity.normalized
            : ((Vector2)a.GetPosition() - b.GetPosition()).normalized;

        Vector2 aRight = RightOf(aDesiredDir);
        Vector2 bRight = RightOf(bDir);
        bool aRightOpen = HasRightLane(a, grid, crowd, aDesiredDir, radius, sideStepDistance);
        bool bRightOpen = HasRightLane(b, grid, crowd, bDir, radius, sideStepDistance);

        OfficeEncounterRole aRole;
        OfficeEncounterRole bRole;
        int passerId = 0;

        if (aRightOpen && bRightOpen)
        {
            aRole = OfficeEncounterRole.MutualRightPass;
            bRole = OfficeEncounterRole.MutualRightPass;
        }
        else if (aRightOpen && !bRightOpen)
        {
            aRole = OfficeEncounterRole.Passer;
            bRole = OfficeEncounterRole.Yielder;
            passerId = aId;
        }
        else if (!aRightOpen && bRightOpen)
        {
            aRole = OfficeEncounterRole.Yielder;
            bRole = OfficeEncounterRole.Passer;
            passerId = bId;
        }
        else if (a.GetRightOfWayPriority() >= b.GetRightOfWayPriority())
        {
            aRole = OfficeEncounterRole.Passer;
            bRole = OfficeEncounterRole.Yielder;
            passerId = aId;
        }
        else
        {
            aRole = OfficeEncounterRole.Yielder;
            bRole = OfficeEncounterRole.Passer;
            passerId = bId;
        }

        return new Encounter
        {
            lowId = lowId,
            highId = highId,
            passerId = passerId,
            lowRole = aId == lowId ? aRole : bRole,
            highRole = aId == highId ? aRole : bRole,
            lowSideDirection = aId == lowId ? aRight : bRight,
            highSideDirection = aId == highId ? aRight : bRight,
            expiresAt = now + encounterLifetime
        };
    }

    private bool HasRightLane(
        AIWorkerAgent worker,
        OfficeGrid2D grid,
        OfficeCrowdCoordinator2D crowd,
        Vector2 forward,
        float radius,
        float sideStepDistance)
    {
        if (forward.sqrMagnitude <= 0.0001f)
            return false;

        Vector2 right = RightOf(forward);
        Vector2 candidate = worker.GetPosition() + right * sideStepDistance;
        return grid.IsBodyPhysicallyClear(candidate, radius) &&
               crowd.IsWorkerMoveClear(worker.GetPosition(), candidate, radius, worker);
    }

    private float GetSpeedMultiplier(OfficeEncounterRole role)
    {
        switch (role)
        {
            case OfficeEncounterRole.MutualRightPass:
                return mutualPassSpeed;
            case OfficeEncounterRole.Passer:
                return passerSpeed;
            case OfficeEncounterRole.Yielder:
                return yielderSpeed;
            default:
                return 1f;
        }
    }

    private static Vector2 RightOf(Vector2 forward)
    {
        return new Vector2(forward.y, -forward.x);
    }

    private static long MakeKey(int a, int b)
    {
        uint low = (uint)Mathf.Min(a, b);
        uint high = (uint)Mathf.Max(a, b);
        return ((long)low << 32) | high;
    }
}
