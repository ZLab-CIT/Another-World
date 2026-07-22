using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class SceneItem : MonoBehaviour
{
    private SpriteRenderer sr;
    private Coroutine scheduledDestroyRoutine;

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
