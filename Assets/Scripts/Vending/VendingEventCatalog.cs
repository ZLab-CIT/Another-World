using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Loads and indexes vending event assets. Selection and lookup live here so the
/// dispatcher can focus on executing events.
/// </summary>
public sealed class VendingEventCatalog
{
    private readonly List<VendingEventSO> events = new();
    private readonly Dictionary<string, VendingEventSO> byEventId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VendingEventSO> byProductId = new(StringComparer.Ordinal);

    public IReadOnlyList<VendingEventSO> Events => events;

    public void LoadFromResources(string resourcePath)
    {
        events.Clear();
        byEventId.Clear();
        byProductId.Clear();

        VendingEventSO[] loaded = Resources.LoadAll<VendingEventSO>(resourcePath);
        if (loaded == null || loaded.Length == 0)
        {
            Debug.LogWarning(nameof(VendingEventCatalog) + ": no event assets found in Resources/" + resourcePath + ".");
            return;
        }

        foreach (VendingEventSO evt in loaded)
            TryAdd(evt);
    }

    public VendingEventSO FindByEventId(string eventId)
    {
        return !string.IsNullOrWhiteSpace(eventId) && byEventId.TryGetValue(eventId, out VendingEventSO evt)
            ? evt
            : null;
    }

    public VendingEventSO FindByProductId(string productId)
    {
        return !string.IsNullOrWhiteSpace(productId) && byProductId.TryGetValue(productId, out VendingEventSO evt)
            ? evt
            : null;
    }

    public VendingEventSO FindFirst(Predicate<VendingEventSO> predicate)
    {
        return predicate == null ? null : events.Find(predicate);
    }

    public VendingEventSO PickWeighted(Predicate<VendingEventSO> predicate, Func<VendingEventSO, float> getWeight)
    {
        List<VendingEventSO> candidates = predicate == null ? new List<VendingEventSO>(events) : events.FindAll(predicate);
        if (candidates.Count == 0)
            return null;

        float total = 0f;
        foreach (VendingEventSO evt in candidates)
            total += Mathf.Max(0f, getWeight(evt));

        if (total <= 0f)
            return candidates[UnityEngine.Random.Range(0, candidates.Count)];

        float roll = UnityEngine.Random.value * total;
        foreach (VendingEventSO evt in candidates)
        {
            roll -= Mathf.Max(0f, getWeight(evt));
            if (roll <= 0f)
                return evt;
        }

        return candidates[candidates.Count - 1];
    }

    private void TryAdd(VendingEventSO evt)
    {
        if (evt == null || string.IsNullOrWhiteSpace(evt.eventId))
            return;

        if (byEventId.ContainsKey(evt.eventId))
        {
            Debug.LogError($"Duplicate vending event id '{evt.eventId}' on {evt.name}; asset skipped.", evt);
            return;
        }

        if (!string.IsNullOrWhiteSpace(evt.physicalProductId) && byProductId.ContainsKey(evt.physicalProductId))
        {
            Debug.LogError($"Duplicate physical product id '{evt.physicalProductId}' on {evt.name}; asset skipped.", evt);
            return;
        }

        events.Add(evt);
        byEventId.Add(evt.eventId, evt);
        if (!string.IsNullOrWhiteSpace(evt.physicalProductId))
            byProductId.Add(evt.physicalProductId, evt);
    }
}
