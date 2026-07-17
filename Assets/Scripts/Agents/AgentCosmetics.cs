using UnityEngine;

[RequireComponent(typeof(AgentPresentation2D))]
public class AgentCosmetics : MonoBehaviour
{
    private AgentPresentation2D presentation;

    private void Awake()
    {
        presentation = GetComponent<AgentPresentation2D>();
    }

    public void ApplyHat(Sprite sprite, HatCatalogSO.HatPool pool, Vector3 scale)
    {
        if (sprite == null || pool == null)
            return;
        presentation.ApplyHat(sprite, pool.standingOffset, pool.sittingOffset, scale);
    }
}
