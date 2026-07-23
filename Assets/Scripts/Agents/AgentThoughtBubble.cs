using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
    [SerializeField, Min(0f)] private float screenEdgePadding = 18f;

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
    private Image backgroundImage;
    private readonly Vector3[] worldCorners = new Vector3[4];

    private void Awake()
    {
        InitializeLayout();
        if (canvasGroup != null)
            canvasGroup.alpha = 0f;
    }

    private void LateUpdate()
    {
        if (canvasGroup != null && canvasGroup.alpha > 0f)
            ClampToScreen();
    }

    public void Show(string content)
    {
        ConfigureThoughtStyle();
        if (!PrepareContent(content))
            return;

        if (activeRoutine != null)
            StopCoroutine(activeRoutine);

        activeRoutine = StartCoroutine(ShowRoutine());
    }

    public void ShowDialogue(string speakerName, string content, Color speakerColor)
    {
        ConfigureSpeechStyle(speakerColor);
        string color = ColorUtility.ToHtmlStringRGB(speakerColor);
        string header = SanitizeRichText(speakerName);
        string body = SanitizeRichText(content);
        if (!PrepareContent("<b><color=#" + color + ">" + header + "</color></b>\n" + body))
            return;

        if (activeRoutine != null)
            StopCoroutine(activeRoutine);

        activeRoutine = StartCoroutine(FadeInOnly());
    }

    public void ConfigureThoughtStyle()
    {
        EnsureBackgroundImage();
        if (backgroundImage != null)
            backgroundImage.color = new Color(1f, 0.97f, 0.78f, 0.98f);
    }

    public void ConfigureSpeechStyle(Color speakerColor)
    {
        EnsureBackgroundImage();
        if (backgroundImage != null)
            backgroundImage.color = Color.Lerp(Color.white, speakerColor, 0.18f);
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

        EnsureBackgroundImage();

        // Keep the bottom of the bubble fixed above the character and let added
        // lines grow upward instead of covering the character.
        float bottom = backgroundRect.anchoredPosition.y
            - backgroundRect.rect.height * backgroundRect.pivot.y;
        backgroundRect.pivot = new Vector2(backgroundRect.pivot.x, 0f);
        backgroundRect.anchoredPosition = new Vector2(backgroundRect.anchoredPosition.x, bottom);
    }

    private void EnsureBackgroundImage()
    {
        if (backgroundImage != null)
            return;
        if (backgroundRect != null)
            backgroundImage = backgroundRect.GetComponent<Image>();
        if (backgroundImage == null)
            backgroundImage = GetComponentInChildren<Image>(true);
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
        Canvas.ForceUpdateCanvases();
        FitWidthToScreen(content);
        ClampToScreen();
    }

    private void FitWidthToScreen(string content)
    {
        Camera camera = Camera.main;
        if (camera == null || messageText == null || messageRect == null || backgroundRect == null)
            return;

        backgroundRect.GetWorldCorners(worldCorners);
        float left = camera.WorldToScreenPoint(worldCorners[0]).x;
        float right = camera.WorldToScreenPoint(worldCorners[2]).x;
        float renderedWidth = right - left;
        float availableWidth = Mathf.Max(60f, Screen.width - screenEdgePadding * 2f);
        if (renderedWidth <= availableWidth)
            return;

        float shrink = Mathf.Clamp01(availableWidth / renderedWidth);
        float textWidth = Mathf.Max(minimumTextWidth, messageRect.rect.width * shrink);
        Vector2 wrappedSize = messageText.GetPreferredValues(content, textWidth, Mathf.Infinity);
        float textHeight = Mathf.Max(messageText.fontSize, wrappedSize.y);

        messageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, textWidth);
        messageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, textHeight);
        backgroundRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, textWidth + horizontalPadding);
        backgroundRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, textHeight + verticalPadding);
        messageText.ForceMeshUpdate();
        Canvas.ForceUpdateCanvases();
    }

    private void ClampToScreen()
    {
        Camera camera = Camera.main;
        if (camera == null || backgroundRect == null)
            return;

        backgroundRect.GetWorldCorners(worldCorners);
        float left = camera.WorldToScreenPoint(worldCorners[0]).x;
        float right = camera.WorldToScreenPoint(worldCorners[2]).x;
        float bottom = camera.WorldToScreenPoint(worldCorners[0]).y;
        float top = camera.WorldToScreenPoint(worldCorners[1]).y;

        float deltaX = 0f;
        if (left < screenEdgePadding)
            deltaX = screenEdgePadding - left;
        else if (right > Screen.width - screenEdgePadding)
            deltaX = Screen.width - screenEdgePadding - right;

        float deltaY = 0f;
        if (bottom < screenEdgePadding)
            deltaY = screenEdgePadding - bottom;
        else if (top > Screen.height - screenEdgePadding)
            deltaY = Screen.height - screenEdgePadding - top;

        if (Mathf.Approximately(deltaX, 0f) && Mathf.Approximately(deltaY, 0f))
            return;

        Vector3 screenPosition = camera.WorldToScreenPoint(transform.position);
        Vector3 shifted = camera.ScreenToWorldPoint(new Vector3(
            screenPosition.x + deltaX,
            screenPosition.y + deltaY,
            screenPosition.z));
        transform.position += shifted - transform.position;
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
