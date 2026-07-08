using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public struct SpriteLifetime
{
    public Sprite sprite;
    public float visibleSeconds;
}

public class RandomSquareSpawner : MonoBehaviour
{
    [Tooltip("Default lifetime used when an entry's visibleSeconds is 0 or less.")]
    [SerializeField] private float visibleSeconds = 2f;
    [SerializeField] private float viewportMargin = 0.08f;
    [SerializeField] private int sortingOrder = 50;

    [Header("Object Sprites")]
    [Tooltip("Sprites that can be spawned, each with its own visible lifetime. A visibleSeconds of 0 or less falls back to the default.")]
    [SerializeField] private List<SpriteLifetime> objectSprites = new List<SpriteLifetime>();
    [Tooltip("World scale applied to spawned sprites (1 = natural sprite size).")]
    [SerializeField] private float objectScale = 1f;

    public void ShowRandomSquare()
    {
        Camera camera = Camera.main;
        if (camera == null)
            return;

        SpriteLifetime entry = PickObjectSprite();
        if (entry.sprite == null)
            return;

        Vector3 position = camera.ViewportToWorldPoint(new Vector3(
            Random.Range(viewportMargin, 1f - viewportMargin),
            Random.Range(viewportMargin, 1f - viewportMargin),
            Mathf.Abs(camera.transform.position.z)));
        position.z = 0f;

        GameObject square = new GameObject("Random " + entry.sprite.name);
        square.transform.position = position;
        square.transform.localScale = Vector3.one * objectScale;

        SpriteRenderer renderer = square.AddComponent<SpriteRenderer>();
        renderer.sprite = entry.sprite;
        renderer.color = Color.white;
        renderer.sortingOrder = sortingOrder;

        float lifetime = entry.visibleSeconds > 0f
            ? entry.visibleSeconds
            : Mathf.Max(0.01f, visibleSeconds);
        StartCoroutine(HideAfterDelay(square, lifetime));
    }

    private SpriteLifetime PickObjectSprite()
    {
        if (objectSprites == null || objectSprites.Count == 0)
            return default;

        return objectSprites[Random.Range(0, objectSprites.Count)];
    }

    private IEnumerator HideAfterDelay(GameObject square, float delay)
    {
        yield return new WaitForSeconds(delay);

        if (square != null)
            Destroy(square);
    }
}
