using UnityEngine;
using System.Collections.Generic;

public enum OfficeActionType
{
    WorkDesk,
    CoffeeMachine,
    BreakSpot,
    ChatSpot,
    MeetingRoom
}

public class OfficeActionPoint : MonoBehaviour
{
    public OfficeActionType actionType;

    [Header("Action Settings")]
    public float useTime = 3f;
    public float baseScore = 10f;

    [Tooltip("How many agents can use this at the same time? (e.g. ChatSpot = 2, WorkDesk = 1)")]
    public int capacity = 1;

    [Tooltip("Used only when capacity is greater than 1. Keeps users separated around this point.")]
    public float multiUserSlotRadius = 0.5f;

    [Tooltip("For single-user points, offset the target away from the marker. Useful when the marker is centered on a desk but workers should stand beside it.")]
    public Vector2 singleUserTargetOffset = Vector2.zero;

    [Header("Effects After Use")]
    public float energyChange = 0f;
    public float focusChange = 0f;
    public float socialChange = 0f;
    public float productivityChange = 0f;

    // Stable reservation slots. A worker keeps the same slot until Release().
    private readonly Dictionary<AIWorkerAgent, int> reservedSlots = new Dictionary<AIWorkerAgent, int>();

    public int CurrentUsers => reservedSlots.Count;

    public bool IsReservedByOther(AIWorkerAgent agent)
    {
        if (agent != null && reservedSlots.ContainsKey(agent))
            return false;

        return reservedSlots.Count >= Mathf.Max(1, capacity);
    }

    public bool TryReserve(AIWorkerAgent agent)
    {
        if (agent == null)
            return false;

        if (reservedSlots.ContainsKey(agent))
            return true;

        int safeCapacity = Mathf.Max(1, capacity);
        if (reservedSlots.Count >= safeCapacity)
            return false;

        for (int slot = 0; slot < safeCapacity; slot++)
        {
            if (!reservedSlots.ContainsValue(slot))
            {
                reservedSlots.Add(agent, slot);
                return true;
            }
        }

        return false;
    }

    public void Release(AIWorkerAgent agent)
    {
        if (agent != null)
            reservedSlots.Remove(agent);
    }

    public Vector2 GetTargetPosition(AIWorkerAgent agent)
    {
        int safeCapacity = Mathf.Max(1, capacity);

        if (agent == null || safeCapacity <= 1)
            return (Vector2)transform.position + singleUserTargetOffset;

        if (!reservedSlots.TryGetValue(agent, out int slot))
            return transform.position;

        float angle = slot * (Mathf.PI * 2f / safeCapacity);
        Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * multiUserSlotRadius;
        return (Vector2)transform.position + offset;
    }

    public void ApplyTo(AIWorkerAgent agent)
    {
        agent.ApplyEffects(
            energyChange,
            focusChange,
            socialChange,
            productivityChange
        );
    }

    private void OnDrawGizmos()
    {
        int currentUsers = reservedSlots != null ? reservedSlots.Count : 0;

        if (currentUsers == 0) Gizmos.color = Color.green;
        else if (currentUsers < Mathf.Max(1, capacity)) Gizmos.color = Color.yellow;
        else Gizmos.color = Color.red;

        Gizmos.DrawWireSphere(transform.position, 0.2f);

        int safeCapacity = Mathf.Max(1, capacity);
        if (safeCapacity > 1)
        {
            Gizmos.color = Color.cyan;
            for (int slot = 0; slot < safeCapacity; slot++)
            {
                float angle = slot * (Mathf.PI * 2f / safeCapacity);
                Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * multiUserSlotRadius;
                Gizmos.DrawWireSphere((Vector2)transform.position + offset, 0.12f);
            }
        }
    }
}
