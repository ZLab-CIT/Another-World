using System.Collections;
using UnityEngine;

public class RandomSquareSpawner : MonoBehaviour
{
    [SerializeField] private float squareSize = 0.6f;
    [SerializeField] private float visibleSeconds = 2f;
    [SerializeField] private float viewportMargin = 0.08f;
    [SerializeField] private Color squareColor = new Color(1f, 0.2f, 0.1f, 1f);
    [SerializeField] private int sortingOrder = 50;

    private static Sprite squareSprite;

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

        GameObject square = new GameObject("Random Square");
        square.transform.position = position;
        square.transform.localScale = Vector3.one * squareSize;

        SpriteRenderer renderer = square.AddComponent<SpriteRenderer>();
        renderer.sprite = GetSquareSprite();
        renderer.color = squareColor;
        renderer.sortingOrder = sortingOrder;

        StartCoroutine(HideAfterDelay(square));
    }

    private IEnumerator HideAfterDelay(GameObject square)
    {
        yield return new WaitForSeconds(visibleSeconds);

        if (square != null)
            Destroy(square);
    }

    private static Sprite GetSquareSprite()
    {
        if (squareSprite != null)
            return squareSprite;

        Texture2D texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();

        squareSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        return squareSprite;
    }
}
