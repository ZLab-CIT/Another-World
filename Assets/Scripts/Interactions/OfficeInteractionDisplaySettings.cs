using UnityEngine;

public enum InteractionDisplayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

[CreateAssetMenu(menuName = "Another World/Interaction Hub/Display Settings",
    fileName = "InteractionHubDisplaySettings")]
public sealed class OfficeInteractionDisplaySettings : ScriptableObject
{
    [Header("Prefab")]
    [Tooltip("Optional authored UI prefab. Its root must have OfficeInteractionDisplayView. Leave empty to use the generated tile.")]
    public OfficeInteractionDisplayView panelPrefab;

    [Header("Placement")]
    [Tooltip("Screen corner used as the tile's anchor.")]
    public InteractionDisplayCorner corner = InteractionDisplayCorner.TopLeft;
    [Tooltip("Distance from the selected corner in screen pixels.")]
    public Vector2 margin = new(24f, 24f);
    [Tooltip("Width and height of the complete QR tile.")]
    public Vector2 panelSize = new(470f, 158f);
    [Tooltip("Width and height of the QR image inside the tile.")]
    public Vector2 qrSize = new(124f, 124f);
    [Header("Typography")]
    [Range(8f, 32f)] public float eyebrowFontSize = 14f;
    [Range(12f, 48f)] public float titleFontSize = 25f;
    [Range(8f, 32f)] public float bodyFontSize = 15f;
    [Range(0.5f, 2f)] public float scale = 1f;
    public int canvasSortingOrder = 900;

    private void OnValidate()
    {
        margin.x = Mathf.Max(0f, margin.x);
        margin.y = Mathf.Max(0f, margin.y);
        panelSize.x = Mathf.Max(280f, panelSize.x);
        panelSize.y = Mathf.Max(110f, panelSize.y);
        qrSize.x = Mathf.Clamp(qrSize.x, 72f, panelSize.y - 20f);
        qrSize.y = Mathf.Clamp(qrSize.y, 72f, panelSize.y - 20f);
        eyebrowFontSize = Mathf.Clamp(eyebrowFontSize, 8f, 32f);
        titleFontSize = Mathf.Clamp(titleFontSize, 12f, 48f);
        bodyFontSize = Mathf.Clamp(bodyFontSize, 8f, 32f);
        scale = Mathf.Clamp(scale, 0.5f, 2f);
    }
}
