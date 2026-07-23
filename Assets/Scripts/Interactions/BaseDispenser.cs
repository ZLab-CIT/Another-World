using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(OfficeActionPoint))]
public abstract class BaseDispenser : MonoBehaviour
{
    [Header("Dispenser Settings")]
    [SerializeField] protected Transform spawnPoint;
    [SerializeField] protected GameObject itemPrefab;
    [SerializeField] protected ObjectSpritesSet spritesSet;
    [SerializeField, Min(0f)] protected float itemLifetime = 15f;

    protected SceneItem lastSpawnedItem;

    public virtual SceneItem Dispense(float? overrideLifetime = null)
    {
        if (spawnPoint == null || spritesSet == null)
        {
            Debug.LogWarning($"Prefab or spawn point not set on {name}.", this);
            return null;
        }

        // Clean up previous item if it wasn't picked up
        if (lastSpawnedItem != null)
            Destroy(lastSpawnedItem.gameObject);

        SceneItem item = CreateItemInstance();
        if (item == null)
        {
            Debug.LogWarning($"{name}: failed to create item instance.", this);
            return null;
        }

        Sprite randomSprite = spritesSet.GetRandomSprite();
        item.SetSprite(randomSprite);
        float life = overrideLifetime ?? itemLifetime;
        item.ScheduleDestroy(life);
        lastSpawnedItem = item;

        return item;
    }

    public virtual SceneItem ConsumeLastItem()
    {
        if (lastSpawnedItem == null)
            return null;

        SceneItem item = lastSpawnedItem;
        lastSpawnedItem = null;
        item.CancelScheduledDestroy();
        return item;
    }

    private SceneItem CreateItemInstance()
    {
        GameObject itemObj;
        if (itemPrefab != null)
            itemObj = Instantiate(itemPrefab, spawnPoint.position, spawnPoint.rotation, spawnPoint);
        else
            return null;

        if (!itemObj.TryGetComponent<SceneItem>(out var sceneItem))
            sceneItem = itemObj.AddComponent<SceneItem>();
        return sceneItem;
    }
}
