using System.Collections;
using TMPro;
using UnityEngine;

public class AgentThoughtBubble : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private TMP_Text messageText;

    [Header("Automatic Size")]
    [SerializeField, Min(1f)] private float minimumTextWidth = 120f;
    [SerializeField, Min(1f)] private float maximumTextWidth = 330f;
    [SerializeField, Min(0f)] private float horizontalPadding = 30f;
    [SerializeField, Min(0f)] private float verticalPadding = 34f;

    [Header("Rendering")]
    [Tooltip("Kept above the character SortingGroups, whose base order is 10000.")]
    [SerializeField, Range(-32768, 32767)] private int canvasSortingOrder = 30000;

    [Header("Animation")]
    [SerializeField, Min(0f)] private float fadeInDuration = 0.2f;
    [SerializeField, Min(0f)] private float holdDuration = 4.5f;
    [SerializeField, Min(0f)] private float fadeOutDuration = 0.6f;

    private Coroutine activeRoutine;
    private RectTransform backgroundRect;
    private RectTransform messageRect;
    private Canvas bubbleCanvas;

    private void Awake()
    {
        InitializeLayout();
        if (canvasGroup != null)
            canvasGroup.alpha = 0f;
    }

    public void Show(string content)
    {
        if (!PrepareContent(content))
            return;

        if (activeRoutine != null)
            StopCoroutine(activeRoutine);

        activeRoutine = StartCoroutine(ShowRoutine());
    }

    public void ShowDialogue(string speakerName, string content, Color speakerColor)
    {
        string color = ColorUtility.ToHtmlStringRGB(speakerColor);
        string header = SanitizeRichText(speakerName);
        string body = SanitizeRichText(content);
        if (!PrepareContent("<b><color=#" + color + ">" + header + "</color></b>\n" + body))
            return;

        if (activeRoutine != null)
            StopCoroutine(activeRoutine);

        activeRoutine = StartCoroutine(FadeInOnly());
    }

    public void Hide()
    {
        if (activeRoutine != null)
            StopCoroutine(activeRoutine);
        activeRoutine = null;
        if (messageText != null)
            messageText.text = string.Empty;
        if (canvasGroup != null)
            canvasGroup.alpha = 0f;
    }

    private void InitializeLayout()
    {
        if (bubbleCanvas == null)
            bubbleCanvas = GetComponentInChildren<Canvas>(true);
        if (bubbleCanvas != null)
        {
            bubbleCanvas.overrideSorting = true;
            bubbleCanvas.sortingLayerName = "Default";
            bubbleCanvas.sortingOrder = canvasSortingOrder;
        }

        if (messageText == null)
            return;

        messageRect = messageText.rectTransform;
        backgroundRect = messageRect.parent as RectTransform;
        messageText.enableWordWrapping = true;
        messageText.overflowMode = TextOverflowModes.Overflow;

        if (backgroundRect == null)
            return;

        // Keep the bottom of the bubble fixed above the character and let added
        // lines grow upward instead of covering the character.
        float bottom = backgroundRect.anchoredPosition.y
            - backgroundRect.rect.height * backgroundRect.pivot.y;
        backgroundRect.pivot = new Vector2(backgroundRect.pivot.x, 0f);
        backgroundRect.anchoredPosition = new Vector2(backgroundRect.anchoredPosition.x, bottom);
    }

    private void ResizeToContent(string content)
    {
        if (messageText == null || messageRect == null || backgroundRect == null)
            InitializeLayout();
        if (messageText == null || messageRect == null || backgroundRect == null)
            return;

        float minWidth = Mathf.Max(1f, minimumTextWidth);
        float maxWidth = Mathf.Max(minWidth, maximumTextWidth);
        Vector2 unwrappedSize = messageText.GetPreferredValues(content, Mathf.Infinity, Mathf.Infinity);
        float textWidth = Mathf.Clamp(unwrappedSize.x, minWidth, maxWidth);
        Vector2 wrappedSize = messageText.GetPreferredValues(content, textWidth, Mathf.Infinity);
        float textHeight = Mathf.Max(messageText.fontSize, wrappedSize.y);

        messageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, textWidth);
        messageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, textHeight);
        backgroundRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, textWidth + horizontalPadding);
        backgroundRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, textHeight + verticalPadding);
        messageText.ForceMeshUpdate();
    }

    private bool PrepareContent(string content)
    {
        if (messageText == null || canvasGroup == null)
            return false;

        messageText.text = content ?? string.Empty;
        ResizeToContent(messageText.text);
        return true;
    }

    private static string SanitizeRichText(string value)
    {
        return (value ?? string.Empty).Replace("<", "‹").Replace(">", "›");
    }

    private IEnumerator FadeInOnly()
    {
        yield return Fade(canvasGroup.alpha, 1f, fadeInDuration);
        activeRoutine = null;
    }

    private IEnumerator ShowRoutine()
    {
        yield return Fade(canvasGroup.alpha, 1f, fadeInDuration);
        yield return new WaitForSecondsRealtime(holdDuration);
        yield return Fade(1f, 0f, fadeOutDuration);

        messageText.text = string.Empty;
        activeRoutine = null;
    }

    private IEnumerator Fade(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            canvasGroup.alpha = to;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        canvasGroup.alpha = to;
    }
}
