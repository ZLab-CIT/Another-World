using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum RandomSpawnShape
{
    Square,
    Circle,
    Triangle,
    Diamond
}

[System.Serializable]
public struct ShapeLifetime
{
    public RandomSpawnShape shape;
    public float visibleSeconds;
}

public class RandomSquareSpawner : MonoBehaviour
{
    [SerializeField] private float squareSize = 0.6f;
    [Tooltip("Default lifetime used when a shape has no override in shapeLifetimes.")]
    [SerializeField] private float visibleSeconds = 2f;
    [SerializeField] private float viewportMargin = 0.08f;
    [SerializeField] private Color squareColor = new Color(1f, 0.2f, 0.1f, 1f);
    [SerializeField] private int sortingOrder = 50;

    [Header("Shape Selection")]
    [Tooltip("Shapes that can be picked when spawning. If empty, Square is used.")]
    [SerializeField] private List<RandomSpawnShape> spawnShapes = new List<RandomSpawnShape>
    {
        RandomSpawnShape.Square,
        RandomSpawnShape.Circle,
        RandomSpawnShape.Triangle,
        RandomSpawnShape.Diamond
    };

    [Header("Per-Shape Lifetime")]
    [Tooltip("Override visibleSeconds per shape. Any shape not listed here uses the default visibleSeconds.")]
    [SerializeField] private List<ShapeLifetime> shapeLifetimes = new List<ShapeLifetime>
    {
        new ShapeLifetime { shape = RandomSpawnShape.Square,   visibleSeconds = 2f },
        new ShapeLifetime { shape = RandomSpawnShape.Circle,   visibleSeconds = 3f },
        new ShapeLifetime { shape = RandomSpawnShape.Triangle, visibleSeconds = 1.5f },
        new ShapeLifetime { shape = RandomSpawnShape.Diamond,  visibleSeconds = 2.5f }
    };

    private static readonly Dictionary<RandomSpawnShape, Sprite> shapeSprites = new Dictionary<RandomSpawnShape, Sprite>();

    public void ShowRandomSquare()
    {
        Camera camera = Camera.main;
        if (camera == null)
            return;

        Vector3 position = camera.ViewportToWorldPoint(new Vector3(
            Random.Range(viewportMargin, 1f - viewportMargin),
            Random.Range(viewportMargin, 1f - viewportMargin),
            Mathf.Abs(camera.transform.position.z)));
        position.z = 0f;

        RandomSpawnShape shape = PickRandomShape();

        GameObject square = new GameObject("Random " + shape);
        square.transform.position = position;
        square.transform.localScale = Vector3.one * squareSize;

        SpriteRenderer renderer = square.AddComponent<SpriteRenderer>();
        renderer.sprite = GetShapeSprite(shape);
        renderer.color = squareColor;
        renderer.sortingOrder = sortingOrder;

        StartCoroutine(HideAfterDelay(square, GetVisibleSeconds(shape)));
    }

    private RandomSpawnShape PickRandomShape()
    {
        if (spawnShapes == null || spawnShapes.Count == 0)
            return RandomSpawnShape.Square;

        return spawnShapes[Random.Range(0, spawnShapes.Count)];
    }

    private float GetVisibleSeconds(RandomSpawnShape shape)
    {
        if (shapeLifetimes != null)
        {
            for (int i = 0; i < shapeLifetimes.Count; i++)
            {
                if (shapeLifetimes[i].shape == shape)
                    return Mathf.Max(0.01f, shapeLifetimes[i].visibleSeconds);
            }
        }

        return Mathf.Max(0.01f, visibleSeconds);
    }

    private IEnumerator HideAfterDelay(GameObject square, float delay)
    {
        yield return new WaitForSeconds(delay);

        if (square != null)
            Destroy(square);
    }

    private static Sprite GetShapeSprite(RandomSpawnShape shape)
    {
        if (shapeSprites.TryGetValue(shape, out Sprite cached) && cached != null)
            return cached;

        Sprite sprite = CreateShapeSprite(shape);
        shapeSprites[shape] = sprite;
        return sprite;
    }

    private static Sprite CreateShapeSprite(RandomSpawnShape shape)
    {
        if (shape == RandomSpawnShape.Square)
            return CreateSquareSprite();

        const int size = 64;
        Texture2D texture = new Texture2D(size, size);
        texture.filterMode = FilterMode.Bilinear;
        Color[] pixels = new Color[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = IsInsideShape(shape, x, y, size);
                pixels[y * size + x] = inside ? Color.white : Color.clear;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();

        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static Sprite CreateSquareSprite()
    {
        Texture2D texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();

        return Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
    }

    private static bool IsInsideShape(RandomSpawnShape shape, int x, int y, int size)
    {
        float cx = (size - 1) * 0.5f;
        float cy = (size - 1) * 0.5f;
        float radius = size * 0.5f;

        switch (shape)
        {
            case RandomSpawnShape.Circle:
            {
                float dx = x - cx;
                float dy = y - cy;
                return dx * dx + dy * dy <= radius * radius;
            }
            case RandomSpawnShape.Triangle:
            {
                float nx = x / (size - 1f);
                float ny = y / (size - 1f);
                return ny >= 0.05f && ny <= 0.95f && (1f - ny) >= Mathf.Abs(nx - 0.5f) * 2f;
            }
            case RandomSpawnShape.Diamond:
            {
                float dx = Mathf.Abs(x - cx);
                float dy = Mathf.Abs(y - cy);
                return dx + dy <= radius;
            }
            default:
                return true;
        }
    }
}
