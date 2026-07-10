using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class VendingEventAnnouncer : MonoBehaviour
{
    [Header("Layout")]
    public float panelWidth = 440f;
    public float panelHeight = 90f;
    public float edgeMargin = 24f;
    public float iconSize = 56f;

    [Header("Animation")]
    public float fadeInDuration = 0.25f;
    public float fadeOutDuration = 0.4f;

    [Header("Colors")]
    public Color panelColor = new Color(0.08f, 0.09f, 0.12f, 0.92f);
    public Color titleColor = Color.white;
    public Color subtitleColor = new Color(0.8f, 0.85f, 0.9f, 1f);

    private CanvasGroup canvasGroup;
    private Image iconImage;
    private TMP_Text titleText;
    private TMP_Text subtitleText;
    private Coroutine activeRoutine;

    private void Awake()
    {
        BuildUI();
    }

    private void BuildUI()
    {
        Canvas canvas = GetComponent<Canvas>();
        if (canvas == null)
        {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            gameObject.AddComponent<CanvasScaler>();
            gameObject.AddComponent<GraphicRaycaster>();
        }

        canvasGroup = gameObject.GetComponent<CanvasGroup>();
        if (canvasGroup == null)
            canvasGroup = gameObject.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;

        RectTransform root = GetComponent<RectTransform>();

        RectTransform panelRect = MakeUI<Image>("AnnouncePanel", root).rectTransform;
        Image panelImage = panelRect.GetComponent<Image>();
        panelImage.color = panelColor;
        panelImage.raycastTarget = false;
        panelRect.anchorMin = new Vector2(0.5f, 1f);
        panelRect.anchorMax = new Vector2(0.5f, 1f);
        panelRect.pivot = new Vector2(0.5f, 1f);
        panelRect.anchoredPosition = new Vector2(0f, -edgeMargin);
        panelRect.sizeDelta = new Vector2(panelWidth, panelHeight);

        iconImage = MakeUI<Image>("Icon", panelRect);
        iconImage.color = Color.white;
        iconImage.raycastTarget = false;
        RectTransform iconRect = iconImage.rectTransform;
        iconRect.anchorMin = new Vector2(0f, 0.5f);
        iconRect.anchorMax = new Vector2(0f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = new Vector2(iconSize * 0.5f + 16f, 0f);
        iconRect.sizeDelta = new Vector2(iconSize, iconSize);

        TMP_FontAsset font = TMP_Settings.defaultFontAsset;

        titleText = MakeUI<TextMeshProUGUI>("Title", panelRect);
        titleText.font = font;
        titleText.fontSize = 26f;
        titleText.fontStyle = FontStyles.Bold;
        titleText.color = titleColor;
        titleText.alignment = TextAlignmentOptions.Left;
        titleText.raycastTarget = false;
        RectTransform titleRect = titleText.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 0.5f);
        titleRect.anchorMax = new Vector2(1f, 0.5f);
        titleRect.pivot = new Vector2(0f, 0f);
        titleRect.anchoredPosition = new Vector2(iconSize + 32f, 2f);
        titleRect.sizeDelta = new Vector2(-(iconSize + 48f), 34f);

        subtitleText = MakeUI<TextMeshProUGUI>("Subtitle", panelRect);
        subtitleText.font = font;
        subtitleText.fontSize = 16f;
        subtitleText.color = subtitleColor;
        subtitleText.alignment = TextAlignmentOptions.Left;
        subtitleText.enableWordWrapping = true;
        subtitleText.raycastTarget = false;
        RectTransform subRect = subtitleText.rectTransform;
        subRect.anchorMin = new Vector2(0f, 0.5f);
        subRect.anchorMax = new Vector2(1f, 0.5f);
        subRect.pivot = new Vector2(0f, 1f);
        subRect.anchoredPosition = new Vector2(iconSize + 32f, -4f);
        subRect.sizeDelta = new Vector2(-(iconSize + 48f), 30f);

        if (font == null)
            Debug.LogWarning(nameof(VendingEventAnnouncer) + ": TMP default font asset is missing. Assign a font in TMP Settings.");
    }

    public void Show(string title, string subtitle, Sprite icon, float holdSeconds)
    {
        if (titleText != null)
            titleText.text = title;
        if (subtitleText != null)
            subtitleText.text = subtitle;
        if (iconImage != null)
        {
            iconImage.sprite = icon;
            iconImage.enabled = icon != null;
        }

        if (activeRoutine != null)
            StopCoroutine(activeRoutine);
        activeRoutine = StartCoroutine(ShowRoutine(holdSeconds));
    }

    private IEnumerator ShowRoutine(float holdSeconds)
    {
        yield return Fade(0f, 1f, fadeInDuration);
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, holdSeconds));
        yield return Fade(1f, 0f, fadeOutDuration);
    }

    private IEnumerator Fade(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            canvasGroup.alpha = to;
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, to, t / duration);
            yield return null;
        }
        canvasGroup.alpha = to;
    }

    private static T MakeUI<T>(string name, Transform parent) where T : Component
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        return obj.AddComponent<T>();
    }
}
