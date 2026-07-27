using UnityEngine;

public enum SceneItemKind
{
    Unknown,
    Coffee,
    Snack
}

[RequireComponent(typeof(SpriteRenderer))]
public class SceneItem : MonoBehaviour
{
    private SpriteRenderer sr;
    private Coroutine scheduledDestroyRoutine;
    public SceneItemKind Kind { get; private set; }

    private void Awake()
    {
        sr = GetComponent<SpriteRenderer>();
    }

    public void SetSprite(Sprite sprite)
    {
        if (sr != null && sprite != null)
        {
            sr.sprite = sprite;
        }
    }

    public void SetKind(SceneItemKind kind)
    {
        Kind = kind;
    }

    public void ScheduleDestroy(float delay)
    {
        CancelScheduledDestroy();
        if (delay <= 0f || !isActiveAndEnabled)
            return;

        scheduledDestroyRoutine = StartCoroutine(DestroyAfterDelay(delay));
    }

    public void CancelScheduledDestroy()
    {
        if (scheduledDestroyRoutine == null)
            return;

        StopCoroutine(scheduledDestroyRoutine);
        scheduledDestroyRoutine = null;
    }

    private System.Collections.IEnumerator DestroyAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        scheduledDestroyRoutine = null;
        Destroy(gameObject);
    }
}
