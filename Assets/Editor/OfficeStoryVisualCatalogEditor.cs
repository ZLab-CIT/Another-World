using System.Collections.Generic;
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(OfficeStoryVisualCatalogSO))]
public sealed class OfficeStoryVisualCatalogEditor : Editor
{
    private readonly List<GameObject> previewObjects = new();
    private int previewStage = -1;

    private void OnEnable()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        ClearPreview();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        bool valuesChanged = EditorGUI.EndChangeCheck();
        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Preview objects are temporary and are never saved. Keep this asset "
            + "selected, then view them in the Scene or Game tab.",
            MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Idle"))
            SetPreviewStage(0);
        if (GUILayout.Button("Noticed"))
            SetPreviewStage(1);
        if (GUILayout.Button("In Progress"))
            SetPreviewStage(2);
        if (GUILayout.Button("Resolved"))
            SetPreviewStage(3);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(previewObjects.Count == 0))
        {
            if (GUILayout.Button("Frame Preview"))
                FramePreview();
            if (GUILayout.Button("Clear Preview"))
                ClearPreview();
        }
        EditorGUILayout.EndHorizontal();

        if (valuesChanged && previewStage >= 0)
            RebuildPreview();
    }

    private void SetPreviewStage(int stage)
    {
        previewStage = stage;
        RebuildPreview();
    }

    private void RebuildPreview()
    {
        ClearPreview(false);
        OfficeStoryVisualCatalogSO catalog =
            (OfficeStoryVisualCatalogSO)target;
        if (catalog?.entries == null)
            return;

        foreach (OfficeStoryVisualEntry entry in catalog.entries)
        {
            Sprite sprite = ResolveSprite(entry, previewStage);
            if (entry == null || sprite == null)
                continue;
            OfficeStoryPropAnchor[] anchors =
                FindObjectsOfType<OfficeStoryPropAnchor>(true)
                    .Where(anchor => anchor != null
                        && string.Equals(anchor.storyId, entry.storyId,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(anchor => anchor.anchorId)
                    .ToArray();
            if (anchors.Length == 0)
            {
                CreatePreview(entry, sprite, null);
                continue;
            }
            foreach (OfficeStoryPropAnchor anchor in anchors)
                CreatePreview(entry, sprite, anchor);
        }

        SceneView.RepaintAll();
        EditorApplication.QueuePlayerLoopUpdate();
    }

    private void CreatePreview(OfficeStoryVisualEntry entry, Sprite sprite,
        OfficeStoryPropAnchor anchor)
    {
        GameObject preview = new("[Preview] " + entry.displayTitle)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        Vector2 position = anchor != null
            ? anchor.PropPosition : entry.worldPosition;
        preview.transform.position = position;
        SpriteRenderer renderer = preview.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingOrder = anchor != null
            && anchor.sortingOrderOverride >= 0
            ? anchor.sortingOrderOverride : entry.sortingOrder;

        float width = Mathf.Max(0.01f, sprite.bounds.size.x);
        float scale = Mathf.Max(0.1f, entry.displayWidth) / width;
        preview.transform.localScale = new Vector3(scale, scale, 1f);
        preview.transform.position -= sprite.bounds.center * scale;
        previewObjects.Add(preview);
    }

    private static Sprite ResolveSprite(
        OfficeStoryVisualEntry entry, int stage)
    {
        if (entry == null)
            return null;
        return stage switch
        {
            0 => entry.visibleWhenInactive ? entry.idleSprite : null,
            1 => entry.noticedSprite,
            2 => entry.progressSprite != null
                ? entry.progressSprite : entry.noticedSprite,
            3 => entry.resolvedSprite != null
                ? entry.resolvedSprite : entry.progressSprite,
            _ => null
        };
    }

    private void FramePreview()
    {
        if (previewObjects.Count == 0
            || SceneView.lastActiveSceneView == null)
            return;

        bool hasBounds = false;
        Bounds bounds = default;
        foreach (GameObject preview in previewObjects)
        {
            if (preview == null
                || !preview.TryGetComponent(out SpriteRenderer renderer))
                continue;
            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (hasBounds)
            SceneView.lastActiveSceneView.Frame(bounds, false);
    }

    private void ClearPreview(bool resetStage = true)
    {
        foreach (GameObject preview in previewObjects)
            if (preview != null)
                DestroyImmediate(preview);
        previewObjects.Clear();
        if (resetStage)
            previewStage = -1;
        SceneView.RepaintAll();
    }

    private void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingEditMode)
            ClearPreview();
    }
}
