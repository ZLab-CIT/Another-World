using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(CanvasGroup))]
public class VendingEventAnnouncer : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private Image iconImage;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text subtitleText;

    [Header("Animation")]
    [SerializeField] private float fadeInDuration = 0.25f;
    [SerializeField] private float fadeOutDuration = 0.4f;

    private CanvasGroup canvasGroup;
    private Coroutine activeRoutine;
    private RectTransform rootRect;
    private Vector2 restingPosition;

    private void Awake()
    {
        canvasGroup = GetComponent<CanvasGroup>();
        rootRect = transform as RectTransform;
        restingPosition = rootRect != null
            ? rootRect.anchoredPosition
            : Vector2.zero;
        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;
    }

    public void Show(string title, string subtitle, Sprite icon, float holdSeconds)
    {
        if (titleText != null)
            titleText.text = CleanText(title, 72);
        if (subtitleText != null)
            subtitleText.text = CleanText(subtitle, 180);
        if (iconImage != null)
        {
            iconImage.sprite = icon;
            iconImage.enabled = icon != null;
            iconImage.preserveAspect = true;
        }

        if (activeRoutine != null)
            StopCoroutine(activeRoutine);
        float readingTime = 3.8f + CountWords(subtitle) * 0.16f;
        activeRoutine = StartCoroutine(ShowRoutine(
            Mathf.Clamp(Mathf.Max(holdSeconds, readingTime), 4.5f, 8f)));
    }

    private IEnumerator ShowRoutine(float holdSeconds)
    {
        if (rootRect != null)
        {
            rootRect.anchoredPosition = restingPosition + Vector2.right * 28f;
            rootRect.localScale = Vector3.one * 0.97f;
        }
        canvasGroup.alpha = 0f;
        yield return Animate(0f, 1f, fadeInDuration, 28f, 0f, 0.97f, 1f);
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, holdSeconds));
        yield return Animate(1f, 0f, fadeOutDuration, 0f, 18f, 1f, 0.98f);
        activeRoutine = null;
    }

    private IEnumerator Animate(float fromAlpha, float toAlpha,
        float duration, float fromOffset, float toOffset,
        float fromScale, float toScale)
    {
        if (duration <= 0f)
        {
            canvasGroup.alpha = toAlpha;
            if (rootRect != null)
            {
                rootRect.anchoredPosition =
                    restingPosition + Vector2.right * toOffset;
                rootRect.localScale = Vector3.one * toScale;
            }
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.SmoothStep(
                0f, 1f, Mathf.Clamp01(elapsed / duration));
            canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, t);
            if (rootRect != null)
            {
                rootRect.anchoredPosition = restingPosition
                    + Vector2.right * Mathf.Lerp(fromOffset, toOffset, t);
                rootRect.localScale = Vector3.one
                    * Mathf.Lerp(fromScale, toScale, t);
            }
            yield return null;
        }
        canvasGroup.alpha = toAlpha;
    }

    private static string CleanText(string value, int maximumCharacters)
    {
        string result = (value ?? "").Replace('\r', ' ')
            .Replace('\n', ' ').Trim();
        if (result.Length <= maximumCharacters)
            return result;
        return result.Substring(0, Mathf.Max(1, maximumCharacters - 1))
            .TrimEnd() + "\u2026";
    }

    private static int CountWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        return value.Split(new[] { ' ', '\t', '\r', '\n' },
            System.StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private void OnValidate()
    {
        fadeInDuration = Mathf.Max(0f, fadeInDuration);
        fadeOutDuration = Mathf.Max(0f, fadeOutDuration);
    }
}
