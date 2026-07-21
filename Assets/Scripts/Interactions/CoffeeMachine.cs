using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(OfficeActionPoint))]
public class CoffeeMachine : MonoBehaviour
{
    [SerializeField] private Transform coffeeSpawnPoint;
    [SerializeField] private GameObject coffeePrefab;
    [SerializeField] private ObjectSpritesSet coffeeSpritesSet;

    private OfficeActionPoint coffeeActionPoint;
    private SceneItem lastSpawnedCup;

    public SceneItem LastSpawnedCup => lastSpawnedCup;

    // Start is called before the first frame update
    void Start()
    {
        if (coffeeSpawnPoint == null) coffeeSpawnPoint = transform.Find("CoffeeSpawnPoint");
        coffeeActionPoint = GetComponent<OfficeActionPoint>();
        coffeeActionPoint.OnActionUsed += SpawnRandomCoffeeCup;
    }

    public void SpawnRandomCoffeeCup()
    {
        if (coffeePrefab == null || coffeeSpawnPoint == null || coffeeSpritesSet == null)
        {
            Debug.LogWarning("Coffee prefab or spawn point not set.");
            return;
        }

        GameObject newCupObj = Instantiate(coffeePrefab, coffeeSpawnPoint.position, coffeeSpawnPoint.rotation, coffeeSpawnPoint);

        SceneItem newCup = newCupObj.GetComponent<SceneItem>();
        Sprite randomSprite = coffeeSpritesSet.GetRandomSprite();
        newCup.SetSprite(randomSprite);
        lastSpawnedCup = newCup;
    }

    public SceneItem ConsumeLastCup()
    {
        SceneItem cup = lastSpawnedCup;
        lastSpawnedCup = null;
        return cup;
    }

    private void OnDestroy()
    {
        if (coffeeActionPoint != null)
        {
            coffeeActionPoint.OnActionUsed -= SpawnRandomCoffeeCup;
        }
    }
}
