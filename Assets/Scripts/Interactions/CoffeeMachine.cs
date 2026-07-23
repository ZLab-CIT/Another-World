using UnityEngine;

[RequireComponent(typeof(OfficeActionPoint))]
public class CoffeeMachine : BaseDispenser
{
    public void SpawnRandomCoffeeCup() => Dispense();
    public SceneItem ConsumeLastCup() => ConsumeLastItem();
}
