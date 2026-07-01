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

    [Header("Effects After Use")]
    public float energyChange = 0f;
    public float focusChange = 0f;
    public float socialChange = 0f;
    public float productivityChange = 0f;
    
    private List<AIWorkerAgent> reservedAgents = new List<AIWorkerAgent>();

    // Number of users here
    public int CurrentUsers => reservedAgents.Count;

    public bool IsReservedByOther(AIWorkerAgent agent)
    {
        if (reservedAgents.Contains(agent)) return false;
        return reservedAgents.Count >= capacity;
    }

    public bool TryReserve(AIWorkerAgent agent)
    {
        if (reservedAgents.Contains(agent)) return true;

        if (reservedAgents.Count >= capacity) return false;

        reservedAgents.Add(agent);
        return true;
    }

    public void Release(AIWorkerAgent agent)
    {
        if (reservedAgents.Contains(agent))
        {
            reservedAgents.Remove(agent);
        }
    }

    public Vector2 GetTargetPosition(AIWorkerAgent agent)
    {
        int index = reservedAgents.IndexOf(agent);

        // If only one agent, put in center
        if (index < 0 || capacity <= 1)
            return transform.position;

        // Radius of circle for several agents
        float radius = 0.5f;

        // Put them in circle
        float angle = index * (Mathf.PI * 2f / capacity);

        Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
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
        // Green - empty, yellow - partically full, red - full
        if (reservedAgents.Count == 0) Gizmos.color = Color.green;
        else if (reservedAgents.Count < capacity) Gizmos.color = Color.yellow;
        else Gizmos.color = Color.red;

        Gizmos.DrawWireSphere(transform.position, 0.2f);
    }
}