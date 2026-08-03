using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class OfficeStoryVisualEntry
{
    public string storyId;
    public string displayTitle;
    public OfficeActionType actionType;
    [Tooltip("Sprite shown while no event is active. Useful for permanent objects such as the whiteboard and router.")]
    public Sprite idleSprite;
    public bool visibleWhenInactive;
    public Sprite noticedSprite;
    public Sprite progressSprite;
    public Sprite resolvedSprite;
    public Vector2 worldPosition;
    [Tooltip("Final rendered width in Unity world units. Reduce this value to make the prop smaller; interaction-slot offsets are configured separately.")]
    [Min(0.1f)] public float displayWidth = 2f;
    public int sortingOrder = 4;
    [Min(2f)] public float useTime = 6f;
    public bool keepUntilNextDay;
    [Tooltip("Hide the prop as soon as the resolving action completes. Use for consumed or removed objects.")]
    public bool hideWhenResolved;
    public List<OfficeActionSlot> slots = new();
}

[CreateAssetMenu(menuName = "Another World/Office Story Visual Catalog",
    fileName = "OfficeStoryVisualCatalog")]
public sealed class OfficeStoryVisualCatalogSO : ScriptableObject
{
    public List<OfficeStoryVisualEntry> entries = new();

    public OfficeStoryVisualEntry Find(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId))
            return null;
        foreach (OfficeStoryVisualEntry entry in entries)
            if (entry != null && string.Equals(entry.storyId, storyId,
                    StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }
}
