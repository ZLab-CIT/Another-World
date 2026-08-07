using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class OfficeStoryWorldController : MonoBehaviour
{
    private sealed class RuntimeProp
    {
        public OfficeStoryVisualEntry entry;
        public GameObject root;
        public Transform visual;
        public SpriteRenderer renderer;
        public OfficeActionPoint actionPoint;
        public readonly List<OfficeStoryPropAnchor> anchors = new();
        public OfficeStoryPropAnchor currentAnchor;
    }

    public static OfficeStoryWorldController Instance { get; private set; }

    private const float ResolvedStageSeconds = 5f;
    private const float NormalAftermathSeconds = 120f;

    private readonly Dictionary<string, RuntimeProp> props =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> noticedDurations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> hiddenAftermath =
        new(StringComparer.OrdinalIgnoreCase);
    private OfficeStoryVisualCatalogSO catalog;
    private bool restored;

    public static OfficeStoryWorldController Ensure()
    {
        if (Instance != null)
            return Instance;
        return new GameObject(nameof(OfficeStoryWorldController))
            .AddComponent<OfficeStoryWorldController>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        catalog = Resources.Load<OfficeStoryVisualCatalogSO>(
            "StoryVisuals/DefaultStoryVisuals");
        foreach (OfficeStoryBeatSO story in
                 Resources.LoadAll<OfficeStoryBeatSO>("OfficeStories"))
            if (story != null && !string.IsNullOrWhiteSpace(story.storyId))
                noticedDurations[story.storyId] =
                    Mathf.Max(1f, story.noticedSeconds);
        BuildProps();
    }

    private void Update()
    {
        if (WorldSimulationPanel.IsPaused)
            return;

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return;
        if (!restored)
        {
            restored = true;
            RestorePersistedVisuals(brain);
        }

        foreach (KeyValuePair<string, RuntimeProp> pair in props)
        {
            PersistedOfficeStoryState state =
                brain.GetOfficeStoryState(pair.Key);
            if (state == null || state.visualStage == 0)
                continue;

            double elapsed = brain.WorldUnixSeconds
                - state.stageChangedWorldTime;
            if (state.visualStage == 1)
            {
                float noticed = noticedDurations.TryGetValue(
                    pair.Key, out float configured) ? configured : 4f;
                if (elapsed >= brain.WorldSecondsFromRealSeconds(noticed))
                {
                    brain.SetOfficeStoryVisualStage(pair.Key, 2);
                    ApplyStage(pair.Value, 2);
                }
            }
            else if (state.visualStage == 3
                && elapsed >= brain.WorldSecondsFromRealSeconds(
                    ResolvedStageSeconds))
            {
                brain.SetOfficeStoryVisualStage(pair.Key, 4);
                ApplyStage(pair.Value, 4);
            }
            else if (state.visualStage == 4
                && !hiddenAftermath.Contains(pair.Key)
                && ShouldHideAftermath(pair.Value.entry, state, brain, elapsed))
            {
                ApplyStage(pair.Value, 0);
                hiddenAftermath.Add(pair.Key);
            }
        }
    }

    public bool SupportsStory(string storyId)
    {
        return catalog != null && catalog.Find(storyId) != null;
    }

    public void ResetWorldState()
    {
        hiddenAftermath.Clear();
        foreach (KeyValuePair<string, RuntimeProp> pair in props)
            ApplyStage(pair.Value, 0);
        restored = false;
    }

    public void ShowStage(string storyId, int stage)
    {
        if (!props.TryGetValue(storyId ?? "", out RuntimeProp prop))
            return;
        hiddenAftermath.Remove(storyId);
        PlaceAtConfiguredAnchor(prop,
            LLMBrainService.Instance?.GetOfficeStoryState(storyId));
        if (stage >= 3 && prop.entry.hideWhenResolved)
        {
            ApplyStage(prop, 0);
            hiddenAftermath.Add(storyId);
            if (stage == 3)
                LLMBrainService.Instance?.SetOfficeStoryVisualStage(
                    storyId, 4);
            return;
        }
        ApplyStage(prop, stage);
    }

    private void BuildProps()
    {
        if (catalog?.entries == null)
            return;
        OfficeStoryPropAnchor[] sceneAnchors =
            FindObjectsOfType<OfficeStoryPropAnchor>(true);
        foreach (OfficeStoryVisualEntry entry in catalog.entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.storyId)
                || props.ContainsKey(entry.storyId))
                continue;

            GameObject root = new("StoryProp_" + entry.storyId);
            root.transform.SetParent(transform, false);
            root.transform.position = entry.worldPosition;

            GameObject visualObject = new("Visual");
            visualObject.transform.SetParent(root.transform, false);
            SpriteRenderer renderer =
                visualObject.AddComponent<SpriteRenderer>();
            renderer.sortingOrder = entry.sortingOrder;

            OfficeActionPoint point = root.AddComponent<OfficeActionPoint>();
            point.actionType = entry.actionType;
            point.useTime = Mathf.Max(2f, entry.useTime);
            point.baseScore = 0f;
            point.slots = entry.slots != null && entry.slots.Count > 0
                ? new List<OfficeActionSlot>(entry.slots)
                : DefaultSlots();
            point.enabled = false;

            RuntimeProp prop = new()
            {
                entry = entry,
                root = root,
                visual = visualObject.transform,
                renderer = renderer,
                actionPoint = point
            };
            foreach (OfficeStoryPropAnchor anchor in sceneAnchors)
                if (anchor != null && string.Equals(anchor.storyId,
                        entry.storyId, StringComparison.OrdinalIgnoreCase))
                    prop.anchors.Add(anchor);
            prop.anchors.Sort((left, right) => string.Compare(
                left.anchorId, right.anchorId,
                StringComparison.OrdinalIgnoreCase));
            props.Add(entry.storyId, prop);
            PlaceAtConfiguredAnchor(prop, null);
            ApplyStage(prop, 0);
        }
    }

    private void ApplyStage(RuntimeProp prop, int stage)
    {
        if (prop == null)
            return;
        Sprite sprite = stage switch
        {
            0 => prop.entry.visibleWhenInactive
                ? prop.entry.idleSprite : null,
            1 => prop.entry.noticedSprite,
            2 => prop.entry.progressSprite != null
                ? prop.entry.progressSprite : prop.entry.noticedSprite,
            3 or 4 => prop.entry.resolvedSprite != null
                ? prop.entry.resolvedSprite : prop.entry.progressSprite,
            _ => null
        };

        prop.renderer.sprite = sprite;
        prop.renderer.enabled = sprite != null;
        prop.renderer.sortingOrder = prop.currentAnchor != null
            && prop.currentAnchor.sortingOrderOverride >= 0
            ? prop.currentAnchor.sortingOrderOverride
            : prop.entry.sortingOrder;
        prop.actionPoint.enabled = stage == 1 || stage == 2;
        if (sprite == null)
            return;

        float width = Mathf.Max(0.01f, sprite.bounds.size.x);
        float scale = Mathf.Max(0.1f, prop.entry.displayWidth) / width;
        prop.visual.localScale = new Vector3(scale, scale, 1f);
        prop.visual.localPosition = -sprite.bounds.center * scale;
    }

    private void RestorePersistedVisuals(LLMBrainService brain)
    {
        foreach (KeyValuePair<string, RuntimeProp> pair in props)
        {
            PersistedOfficeStoryState state =
                brain.GetOfficeStoryState(pair.Key);
            PlaceAtConfiguredAnchor(pair.Value, state);
            int stage = state != null ? state.visualStage : 0;
            if (stage >= 3 && pair.Value.entry.hideWhenResolved)
            {
                ApplyStage(pair.Value, 0);
                hiddenAftermath.Add(pair.Key);
            }
            else
            {
                ApplyStage(pair.Value, stage);
            }
        }
    }

    private static void PlaceAtConfiguredAnchor(RuntimeProp prop,
        PersistedOfficeStoryState state)
    {
        if (prop == null)
            return;
        if (prop.anchors.Count == 0)
        {
            prop.currentAnchor = null;
            prop.root.transform.position = prop.entry.worldPosition;
            return;
        }

        int occurrence = state != null ? Mathf.Max(0, state.started - 1) : 0;
        int index = occurrence % prop.anchors.Count;
        prop.currentAnchor = prop.anchors[index];
        prop.root.transform.position = prop.currentAnchor.PropPosition;
    }

    private static bool ShouldHideAftermath(OfficeStoryVisualEntry entry,
        PersistedOfficeStoryState state, LLMBrainService brain, double elapsed)
    {
        if (entry.hideWhenResolved)
            return true;
        if (entry.keepUntilNextDay)
        {
            DateTime changed = DateTimeOffset.FromUnixTimeSeconds(
                (long)Math.Max(0d, state.stageChangedWorldTime)).LocalDateTime;
            return brain.WorldDateTime.Date > changed.Date;
        }
        return elapsed >= brain.WorldSecondsFromRealSeconds(
            NormalAftermathSeconds);
    }

    private static List<OfficeActionSlot> DefaultSlots()
    {
        return new List<OfficeActionSlot>
        {
            new() { offset = new Vector2(-0.7f, -0.65f),
                facing = OfficeFacingDirection.Up },
            new() { offset = new Vector2(0.7f, -0.65f),
                facing = OfficeFacingDirection.Up },
            new() { offset = new Vector2(-1.1f, 0f),
                facing = OfficeFacingDirection.Right },
            new() { offset = new Vector2(1.1f, 0f),
                facing = OfficeFacingDirection.Left }
        };
    }

}
