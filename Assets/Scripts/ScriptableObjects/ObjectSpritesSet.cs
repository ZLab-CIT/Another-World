using UnityEngine;

[CreateAssetMenu(fileName = "ObjectSpritesSet", menuName = "ZhipuOffice/ObjectSpritesSet")]
public class ObjectSpritesSet : ScriptableObject
{
    public Sprite[] sprites;
    public Sprite GetRandomSprite()
    {
        if (sprites == null || sprites.Length == 0)
        {
            Debug.LogWarning("No sprites available in the ObjectSpritesSet.");
            return null;
        }

        int randIndex = Random.Range(0, sprites.Length);
        return sprites[randIndex];
    }
}