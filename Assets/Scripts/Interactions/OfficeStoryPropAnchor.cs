using UnityEngine;

[ExecuteAlways]
public sealed class OfficeStoryPropAnchor : MonoBehaviour
{
    [Tooltip("Story id from the visual catalog, such as whiteboard_session, wifi_blip, unexpected_package, or tiny_win.")]
    public string storyId;
    [Tooltip("Unique readable name. It defines stable ordering when a story has several possible anchors.")]
    public string anchorId;
    [Tooltip("Fine visual offset from this transform without moving the anchor gizmo.")]
    public Vector2 propOffset;
    [Tooltip("Use -1 to keep the catalog sorting order, or set an explicit order for this location.")]
    public int sortingOrderOverride = -1;

    public Vector2 PropPosition => (Vector2)transform.position + propOffset;

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.65f, 0.15f, 0.9f);
        Gizmos.DrawWireCube(PropPosition, new Vector3(0.35f, 0.35f, 0f));
        Gizmos.DrawLine(transform.position, PropPosition);
    }
}
