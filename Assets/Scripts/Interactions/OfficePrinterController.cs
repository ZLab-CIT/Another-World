using System.Collections;
using UnityEngine;

public sealed class OfficePrinterController : MonoBehaviour
{
    [SerializeField] private SpriteRenderer printerRenderer;
    [SerializeField] private Sprite idleSprite;
    [SerializeField] private Sprite printingSprite;
    [SerializeField] private Sprite jammedSprite;
    [SerializeField] private Sprite resolvedSprite;
    [SerializeField, Min(5f)] private float resolvedDisplaySeconds = 45f;

    private Coroutine resetRoutine;

    private void Awake()
    {
        if (printerRenderer == null)
            printerRenderer = GetComponentInChildren<SpriteRenderer>();
        ShowIdle();
    }

    public void SetStoryStage(int stage)
    {
        StopReset();
        if (printerRenderer == null)
            return;

        if (stage <= 0)
        {
            SetVisual(jammedSprite, Color.white);
            return;
        }
        if (stage == 1)
        {
            SetVisual(printingSprite, Color.white);
            return;
        }

        SetVisual(resolvedSprite, Color.white);
        resetRoutine = StartCoroutine(ResetAfterDelay());
    }

    private IEnumerator ResetAfterDelay()
    {
        yield return new WaitForSeconds(resolvedDisplaySeconds);
        resetRoutine = null;
        ShowIdle();
    }

    private void ShowIdle()
    {
        StopReset();
        SetVisual(idleSprite, Color.white);
    }

    private void SetVisual(Sprite sprite, Color color)
    {
        if (printerRenderer == null)
            return;
        if (sprite != null)
            printerRenderer.sprite = sprite;
        printerRenderer.color = color;
    }

    private void StopReset()
    {
        if (resetRoutine == null)
            return;
        StopCoroutine(resetRoutine);
        resetRoutine = null;
    }
}
