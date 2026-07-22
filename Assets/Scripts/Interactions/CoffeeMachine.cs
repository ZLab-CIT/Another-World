using UnityEngine;

[RequireComponent(typeof(OfficeActionPoint))]
public class CoffeeMachine : MonoBehaviour
{
    [SerializeField] private Transform coffeeSpawnPoint;
    [SerializeField] private GameObject coffeePrefab;
    [SerializeField] private ObjectSpritesSet coffeeSpritesSet;
    [SerializeField, Min(0f)] private float spawnLifetime = 15f;

    private SceneItem lastSpawnedCup;

    // Start is called before the first frame update
    void Start()
    {
        if (coffeeSpawnPoint == null) coffeeSpawnPoint = transform.Find("CoffeeSpawnPoint");
    }

    public void SpawnRandomCoffeeCup()
    {
        if (coffeeSpawnPoint == null || coffeeSpritesSet == null)
        {
            Debug.LogWarning("Coffee prefab or spawn point not set.");
            return;
        }

        if (lastSpawnedCup != null)
            Destroy(lastSpawnedCup.gameObject);

        SceneItem newCup = CreateCupInstance();
        if (newCup == null)
        {
            Debug.LogWarning($"{name}: failed to create coffee cup instance.", this);
            return;
        }

        Sprite randomSprite = coffeeSpritesSet.GetRandomSprite();
        newCup.SetSprite(randomSprite);
        newCup.ScheduleDestroy(spawnLifetime);
        lastSpawnedCup = newCup;
    }

    public SceneItem ConsumeLastCup()
    {
        if (lastSpawnedCup == null)
            return null;

        SceneItem cup = lastSpawnedCup;
        lastSpawnedCup = null;
        cup.CancelScheduledDestroy();
        return cup;
    }

    private SceneItem CreateCupInstance()
    {
        GameObject newCupObj;
        if (coffeePrefab != null)
        {
            newCupObj = Instantiate(coffeePrefab, coffeeSpawnPoint.position, coffeeSpawnPoint.rotation, coffeeSpawnPoint);
        }
        else
        {
            newCupObj = new GameObject("CoffeeCup");
            newCupObj.transform.SetParent(coffeeSpawnPoint, false);
            newCupObj.transform.localPosition = Vector3.zero;
            newCupObj.transform.localRotation = Quaternion.identity;
            newCupObj.transform.localScale = Vector3.one * 0.5f;
            newCupObj.AddComponent<SpriteRenderer>();
        }

        SceneItem sceneItem = newCupObj.GetComponent<SceneItem>();
        if (sceneItem == null)
            sceneItem = newCupObj.AddComponent<SceneItem>();
        return sceneItem;
    }
}
