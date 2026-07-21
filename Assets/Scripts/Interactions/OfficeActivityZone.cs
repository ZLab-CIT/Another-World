using System.Collections.Generic;
using UnityEngine;

public enum OfficeZoneType
{
    General,
    Quiet,
    Lounge,
    WorkArea,
    Corridor
}

[RequireComponent(typeof(Collider2D))]
public class OfficeActivityZone : MonoBehaviour
{
    public OfficeZoneType zoneType = OfficeZoneType.General;
    [Tooltip("Optional activities allowed here. An empty list allows any flexible activity.")]
    public List<OfficeActionType> supportedActivities = new();
    [SerializeField, Min(1)] private int sampleAttempts = 16;

    private Collider2D area;

    private void Awake()
    {
        area = GetComponent<Collider2D>();
    }

    public bool Supports(OfficeActionType actionType)
    {
        return supportedActivities == null || supportedActivities.Count == 0
            || supportedActivities.Contains(actionType);
    }

    public bool MatchesHint(string hint)
    {
        return string.IsNullOrWhiteSpace(hint)
            || string.Equals(zoneType.ToString(), hint.Trim(), System.StringComparison.OrdinalIgnoreCase)
            || name.IndexOf(hint.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public bool TrySamplePosition(OfficeGrid2D grid, float agentRadius, out Vector2 position)
    {
        if (area == null)
            area = GetComponent<Collider2D>();
        if (area == null || grid == null)
        {
            position = transform.position;
            return false;
        }

        Bounds bounds = area.bounds;
        for (int i = 0; i < sampleAttempts; i++)
        {
            Vector2 candidate = new(
                Random.Range(bounds.min.x, bounds.max.x),
                Random.Range(bounds.min.y, bounds.max.y));
            if (!area.OverlapPoint(candidate) || !grid.IsBodyPositionClear(candidate, agentRadius))
                continue;

            position = candidate;
            return true;
        }

        position = transform.position;
        return grid.IsBodyPositionClear(position, agentRadius);
    }

    private void OnValidate()
    {
        Collider2D zoneCollider = GetComponent<Collider2D>();
        if (zoneCollider != null)
            zoneCollider.isTrigger = true;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = zoneType == OfficeZoneType.Quiet
            ? new Color(0.25f, 0.65f, 1f, 0.25f)
            : new Color(0.25f, 1f, 0.55f, 0.2f);
        Collider2D zoneCollider = GetComponent<Collider2D>();
        if (zoneCollider != null)
            Gizmos.DrawCube(zoneCollider.bounds.center, zoneCollider.bounds.size);
    }
}
