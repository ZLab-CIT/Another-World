using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Connects an authored interaction-tile prefab to its runtime content.</summary>
public sealed class OfficeInteractionDisplayView : MonoBehaviour
{
    [SerializeField] private RectTransform panel;
    [SerializeField] private RawImage qrImage;
    [Tooltip("Preferred: one continuous text box for the eyebrow, title, and body.")]
    [SerializeField] private TMP_Text combinedText;
    [Header("Legacy separate text boxes")]
    [SerializeField] private TMP_Text eyebrow;
    [SerializeField] private TMP_Text title;
    [SerializeField] private TMP_Text body;

    public RectTransform Panel => panel != null ? panel : transform as RectTransform;
    public RawImage QrImage => qrImage;
    public TMP_Text CombinedText => combinedText;
    public TMP_Text Eyebrow => eyebrow;
    public TMP_Text Title => title;
    public TMP_Text Body => body;

    public bool IsConfigured => Panel != null && qrImage != null
        && (combinedText != null || (eyebrow != null && title != null && body != null));
}
