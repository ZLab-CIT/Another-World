using System.Collections;
using UnityEngine;

public class VendingMachineAvatar : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform shakeTarget;

    [Tooltip("How far the product falls below the dispense point before landing.")]
    [SerializeField] private float fallDistance = 0.35f;

    [Header("Shake")]
    [SerializeField] private float shakeDuration = 0.3f;
    [SerializeField] private float shakeAngle = 5f;
    [SerializeField] private float shakeFrequency = 38f;

    [Header("Eject")]
    [SerializeField] private float popDuration = 0.16f;
    [SerializeField] private float ejectUpVelocity = 0.9f;
    [SerializeField] private float gravity = -9f;
    [SerializeField] private float landSquash = 0.35f;
    [SerializeField] private float landRecoverDuration = 0.12f;
    [SerializeField] private float fadeDuration = 0.4f;
    [SerializeField] private int productSortingOrder = 60;

    private Quaternion initialLocalRotation;
    private Coroutine activeSequence;
    private SceneItem currentItem;
    private Vector3 currentItemScale = Vector3.one;

    private void Awake()
    {
        if (shakeTarget == null)
        {
            SpriteRenderer sr = GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
                shakeTarget = sr.transform;
        }

        if (shakeTarget == null)
            shakeTarget = transform;

        initialLocalRotation = shakeTarget.localRotation;
    }

    public void PlayDropAnimation(SceneItem item, float lifetime)
    {
        CancelAnimation(true);
        currentItem = item;
        activeSequence = StartCoroutine(PlaySequence(item, lifetime));
    }

    public void CancelAnimation(bool destroyCurrentItem = false)
    {
        if (activeSequence != null)
        {
            StopCoroutine(activeSequence);
            activeSequence = null;
        }

        // If the item got picked up mid-fade, restore its full visibility
        if (currentItem != null)
        {
            currentItem.transform.localScale = currentItemScale;

            if (currentItem.TryGetComponent<SpriteRenderer>(out var sr))
            {
                Color c = sr.color;
                c.a = 1f;
                sr.color = c;
            }

            if (destroyCurrentItem)
                Destroy(currentItem.gameObject);

            currentItem = null;
        }
    }

    private IEnumerator PlaySequence(SceneItem item, float lifetime)
    {
        if (item == null) yield break;

        // Keep the configured spawn point position, but hide the item during the shake.
        GameObject dropObj = item.gameObject;
        dropObj.transform.SetParent(transform, true);
        Vector3 startPos = dropObj.transform.localPosition;
        currentItemScale = dropObj.transform.localScale;
        dropObj.transform.localScale = Vector3.zero;

        SpriteRenderer sr = dropObj.GetComponent<SpriteRenderer>();
        if (sr != null) sr.sortingOrder = productSortingOrder;

        yield return Shake();

        if (item == null) yield break;

        // Animations
        yield return PopIn(dropObj.transform, currentItemScale);
        if (item == null) yield break;
        yield return Fall(dropObj.transform, startPos.y - fallDistance);
        if (item == null) yield break;
        yield return Squash(dropObj.transform);
        if (item == null) yield break;

        // Wait on floor
        float restTime = Mathf.Max(0f, lifetime - shakeDuration - popDuration - 0.5f - fadeDuration);
        if (restTime > 0f)
            yield return new WaitForSeconds(restTime);

        if (sr != null && item != null)
        {
            yield return FadeOut(sr, fadeDuration);
        }

        if (item != null)
        {
            Destroy(item.gameObject);
        }

        if (currentItem == item)
            currentItem = null;

        activeSequence = null;
    }

    private IEnumerator Shake()
    {
        Transform t = shakeTarget;
        float elapsed = 0f;
        while (elapsed < shakeDuration)
        {
            elapsed += Time.deltaTime;
            float fade = 1f - (elapsed / shakeDuration);
            float angle = Mathf.Sin(elapsed * shakeFrequency) * fade * shakeAngle;
            t.localRotation = initialLocalRotation * Quaternion.Euler(0f, 0f, angle);
            yield return null;
        }
        t.localRotation = initialLocalRotation;
    }

    private IEnumerator PopIn(Transform t, Vector3 targetScale)
    {
        if (t == null)
            yield break;

        float elapsed = 0f;
        while (elapsed < popDuration)
        {
            if (t == null)
                yield break;

            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / popDuration);
            t.localScale = targetScale * EaseOutBack(k);
            yield return null;
        }

        if (t != null)
            t.localScale = targetScale;
    }

    private IEnumerator Fall(Transform t, float landY)
    {
        if (t == null)
            yield break;

        Vector3 pos = t.localPosition;
        float velocity = ejectUpVelocity;
        while (pos.y > landY)
        {
            if (t == null)
                yield break;

            velocity += gravity * Time.deltaTime;
            pos.y += velocity * Time.deltaTime;
            if (pos.y < landY)
                pos.y = landY;
            t.localPosition = pos;
            yield return null;
        }
    }

    private IEnumerator Squash(Transform t)
    {
        if (t == null)
            yield break;

        Vector3 baseScale = t.localScale;
        Vector3 squashed = new(baseScale.x * (1f + landSquash), baseScale.y * (1f - landSquash), baseScale.z);
        float elapsed = 0f;
        while (elapsed < landRecoverDuration)
        {
            if (t == null)
                yield break;

            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / landRecoverDuration);
            t.localScale = Vector3.Lerp(squashed, baseScale, EaseOutCubic(k));
            yield return null;
        }

        if (t != null)
            t.localScale = baseScale;
    }

    private IEnumerator FadeOut(SpriteRenderer sr, float duration)
    {
        if (sr == null)
            yield break;

        Color c = sr.color;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (sr == null)
                yield break;

            elapsed += Time.deltaTime;
            c.a = 1f - Mathf.Clamp01(elapsed / duration);
            sr.color = c;
            yield return null;
        }
    }

    private static float EaseOutBack(float k)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f);
    }

    private static float EaseOutCubic(float k)
    {
        return 1f - Mathf.Pow(1f - k, 3f);
    }
}
