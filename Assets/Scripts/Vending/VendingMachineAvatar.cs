using System.Collections;
using UnityEngine;

public class VendingMachineAvatar : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Transform that wobbles when dispensing. Auto-set to the first child SpriteRenderer if empty.")]
    [SerializeField] private Transform shakeTarget;

    [Header("Dispense Point")]
    [Tooltip("Local offset from the machine root where the product ejects.")]
    [SerializeField] private Vector2 dispenseOffset = new(0f, -0.5f);
    [Tooltip("How far the product falls below the dispense point before landing.")]
    [SerializeField] private float fallDistance = 0.35f;

    [Header("Shake")]
    [SerializeField] private float shakeDuration = 0.3f;
    [SerializeField] private float shakeAngle = 5f;
    [SerializeField] private float shakeFrequency = 38f;

    [Header("Eject")]
    [Tooltip("Multiplier applied to the product scale. Lower to make dropped items smaller.")]
    [SerializeField] private float productScaleMultiplier = 1f;
    [SerializeField] private float popDuration = 0.16f;
    [SerializeField] private float ejectUpVelocity = 0.9f;
    [SerializeField] private float gravity = -9f;
    [SerializeField] private float landSquash = 0.35f;
    [SerializeField] private float landRecoverDuration = 0.12f;
    [SerializeField] private float fadeDuration = 0.4f;
    [SerializeField] private int productSortingOrder = 60;

    private Quaternion initialLocalRotation;

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

    public void React(Sprite productSprite, float lifetime, float scale)
    {
        StartCoroutine(PlaySequence(productSprite, lifetime, scale));
    }

    private IEnumerator PlaySequence(Sprite sprite, float lifetime, float scale)
    {
        yield return Shake();

        if (sprite == null)
            yield break;

        GameObject drop = new("VendingDrop_" + sprite.name);
        drop.transform.SetParent(transform, false);
        Vector3 startPos = new(dispenseOffset.x, dispenseOffset.y, 0f);
        drop.transform.localPosition = startPos;
        float finalScale = Mathf.Max(0.05f, scale * productScaleMultiplier);
        drop.transform.localScale = Vector3.one * finalScale;

        SpriteRenderer sr = drop.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = Color.white;
        sr.sortingOrder = productSortingOrder;

        yield return PopIn(drop.transform, finalScale);
        yield return Fall(drop.transform, startPos.y - fallDistance);
        yield return Squash(drop.transform);

        float rest = Mathf.Max(0f, lifetime - shakeDuration - popDuration - 0.5f);
        if (rest > 0f)
            yield return new WaitForSeconds(rest);

        yield return FadeOut(sr, fadeDuration);

        if (drop != null)
            Destroy(drop);
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

    private IEnumerator PopIn(Transform t, float targetScale)
    {
        float elapsed = 0f;
        while (elapsed < popDuration)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / popDuration);
            t.localScale = Vector3.one * (targetScale * EaseOutBack(k));
            yield return null;
        }
        t.localScale = Vector3.one * targetScale;
    }

    private IEnumerator Fall(Transform t, float landY)
    {
        Vector3 pos = t.localPosition;
        float velocity = ejectUpVelocity;
        while (pos.y > landY)
        {
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
        Vector3 baseScale = t.localScale;
        Vector3 squashed = new(baseScale.x * (1f + landSquash), baseScale.y * (1f - landSquash), baseScale.z);
        float elapsed = 0f;
        while (elapsed < landRecoverDuration)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / landRecoverDuration);
            t.localScale = Vector3.Lerp(squashed, baseScale, EaseOutCubic(k));
            yield return null;
        }
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
