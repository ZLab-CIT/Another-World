using System.Collections.Generic;
using UnityEngine;

public enum OfficeActionType
{
    WorkDesk,
    CoffeeMachine,
    VendingMachine,
    BreakSpot,
    ChatSpot,
    MeetingRoom,
    PhoneCall,
    Printer,
    Whiteboard,
    PlantCare,
    WindowBreak,
    WalkAround,
    Think,
    CheckPhone,
    ApproachColleague,
    Custom
}

public enum OfficeFacingDirection
{
    Down,
    Up,
    Left,
    Right
}

[System.Serializable]
public struct OfficeActionSlot
{
    public Vector2 offset;
    public OfficeFacingDirection facing;
}

public class OfficeActionPoint : MonoBehaviour
{
    public OfficeActionType actionType;

    [Header("Action Settings")]
    public float useTime = 3f;
    public float baseScore = 10f;

    [Tooltip("Explicit destinations available at this point. The number of slots is its capacity.")]
    public List<OfficeActionSlot> slots = new()

    {
        new OfficeActionSlot { offset = Vector2.zero, facing = OfficeFacingDirection.Down }
    };

    [Header("Optional Item Placement")]
    [Tooltip("If assigned, held items will be placed here instead of directly on the action point.")]
    public Transform itemPlacementPoint;

    [Header("Effects After Use")]
    public float energyChange;
    public float focusChange;
    public float socialChange;
    public float productivityChange;

    private readonly Dictionary<AIWorkerAgent, int> reservedSlots = new();

    public int CurrentUsers => reservedSlots.Count;

    private bool IsReservedBy(AIWorkerAgent agent)
    {
        return agent != null && reservedSlots.ContainsKey(agent);
    }

    public bool IsReservedByOther(AIWorkerAgent agent)
    {
        return !IsReservedBy(agent) && reservedSlots.Count >= SlotCount;
    }

    public bool TryReserve(AIWorkerAgent agent)
    {
        if (agent == null)
            return false;

        if (reservedSlots.ContainsKey(agent))
            return true;

        if (reservedSlots.Count >= SlotCount)
            return false;

        for (int slot = 0; slot < SlotCount; slot++)
        {
            if (reservedSlots.ContainsValue(slot))
                continue;

            reservedSlots.Add(agent, slot);
            return true;
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
        int slot = agent != null && reservedSlots.TryGetValue(agent, out int reservedSlot)
            ? reservedSlot
            : 0;
        return GetSlotPosition(slot);
    }

    public Vector2 GetFacingVector(AIWorkerAgent agent)
    {
        int slot = agent != null && reservedSlots.TryGetValue(agent, out int reservedSlot)
            ? reservedSlot
            : 0;
        return FacingToVector(slots[slot].facing);
    }

    public void ApplyTo(AIWorkerAgent agent)
    {
        agent.ApplyEffects(energyChange, focusChange, socialChange, productivityChange);
    }

    public Transform GetItemPlacementTransform()
    {
        if (itemPlacementPoint != null)
            return itemPlacementPoint;

        Transform childPoint = transform.Find("TablePoint")
            ?? transform.Find("CoffeePoint")
            ?? transform.Find("CupPoint")
            ?? transform.Find("DeskCoffee")
            ?? transform.Find("ItemPoint");

        if (childPoint != null)
            return childPoint;

        if (transform.parent != null)
        {
            Transform siblingPoint = transform.parent.Find("TablePoint")
                ?? transform.parent.Find("CoffeePoint")
                ?? transform.parent.Find("CupPoint")
                ?? transform.parent.Find("DeskCoffee")
                ?? transform.parent.Find("ItemPoint");

            if (siblingPoint != null)
                return siblingPoint;
        }

        return transform;
    }

    private int SlotCount => slots != null && slots.Count > 0 ? slots.Count : 1;

    private Vector2 GetSlotPosition(int slot)
    {
        Vector2 offset = slots != null && slot >= 0 && slot < slots.Count
            ? slots[slot].offset
            : Vector2.zero;
        return (Vector2)transform.position + offset;
    }

    private static Vector2 FacingToVector(OfficeFacingDirection direction)
    {
        switch (direction)
        {
            case OfficeFacingDirection.Up: return Vector2.up;
            case OfficeFacingDirection.Left: return Vector2.left;
            case OfficeFacingDirection.Right: return Vector2.right;
            default: return Vector2.down;
        }
    }

    private void OnDrawGizmos()
    {
        int currentUsers = reservedSlots != null ? reservedSlots.Count : 0;
        Gizmos.color = currentUsers == 0
            ? Color.green
            : currentUsers < SlotCount ? Color.yellow : Color.red;
        Gizmos.DrawWireSphere(transform.position, 0.2f);

        for (int slot = 0; slot < SlotCount; slot++)
        {
            Vector2 target = GetSlotPosition(slot);
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(target, 0.12f);
            Gizmos.color = Color.blue;
            OfficeFacingDirection direction = slots != null && slot < slots.Count
                ? slots[slot].facing
                : OfficeFacingDirection.Down;
            Gizmos.DrawLine(target, target + FacingToVector(direction) * 0.4f);
        }
    }

    private void OnValidate()
    {
        if (slots == null || slots.Count == 0)
        {
            slots = new List<OfficeActionSlot>
            {
                new() { offset = Vector2.zero, facing = OfficeFacingDirection.Down }
            };
        }
    }
}
