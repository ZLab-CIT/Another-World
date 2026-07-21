using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class SceneItem : MonoBehaviour
{
    private SpriteRenderer sr;

    private void Awake()
    {
        sr = GetComponent<SpriteRenderer>();
    }

    public void SetSprite(Sprite sprite)
    {
        if (sr != null && sprite != null)
        {
            sr.sprite = sprite;
        }
    }

    public void DestroyItem()
    {
        Destroy(gameObject);
    }
}