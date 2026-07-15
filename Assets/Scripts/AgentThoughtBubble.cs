using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AgentThoughtBubble : MonoBehaviour
{
    [Header("Layout")]
    public float pixelsPerUnit = 100f;
    public float width = 380f;
    public float height = 120f;
    [Tooltip("World-space offset above the agent's pivot (bubble grows upward from here).")]
    public float headOffset = 1.1f;

    [Header("Animation")]
    public float fadeInDuration = 0.2f;
    public float holdSeconds = 4.5f;
    public float fadeOutDuration = 0.6f;

    [Header("Colors")]
    public Color bubbleColor = new Color(0.08f, 0.09f, 0.12f, 0.92f);
    public Color textColor = Color.white;

    private CanvasGroup canvasGroup;
    private TMP_Text text;
    private Coroutine activeRoutine;

    private void Awake()
    {
        BuildUI();
    }

    private void BuildUI()
    {
        Canvas canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 50;

        float s = 1f / Mathf.Max(1f, pixelsPerUnit);
        transform.localScale = new Vector3(s, s, s);
        transform.localPosition = new Vector3(0f, headOffset, 0f);

        canvasGroup = gameObject.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;

        RectTransform root = canvas.GetComponent<RectTransform>();
        root.sizeDelta = new Vector2(width, height);
        root.pivot = new Vector2(0.5f, 0f);

        Image panel = MakeUI<Image>("BubbleBg", root);
        panel.color = bubbleColor;
        panel.raycastTarget = false;
        RectTransform panelRect = panel.rectTransform;
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        text = MakeUI<TextMeshProUGUI>("Thought", panelRect);
        text.font = TMP_Settings.defaultFontAsset;
        text.fontSize = 40f;
        text.color = textColor;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        RectTransform tr = text.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.pivot = new Vector2(0.5f, 0.5f);
        tr.offsetMin = new Vector2(10f, 8f);
        tr.offsetMax = new Vector2(-10f, -8f);

        if (TMP_Settings.defaultFontAsset == null)
            Debug.LogWarning(nameof(AgentThoughtBubble) + ": TMP default font asset is missing. Assign one in TMP Settings.");
    }

    public void Show(string content)
    {
        if (text == null)
            return;

        text.text = content ?? "";

        if (activeRoutine != null)
            StopCoroutine(activeRoutine);
        activeRoutine = StartCoroutine(ShowRoutine());
    }

    private IEnumerator ShowRoutine()
    {
        yield return Fade(0f, 1f, fadeInDuration);
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, holdSeconds));
        yield return Fade(1f, 0f, fadeOutDuration);
        activeRoutine = null;
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
