using UnityEngine;

[RequireComponent(typeof(OfficeActionPoint))]
public class CoffeeMachine : BaseDispenser
{
    public void SpawnRandomCoffeeCup() => Dispense();
    public SceneItem ConsumeLastCup() => ConsumeLastItem();

    public override SceneItem Dispense(float? overrideLifetime = null)
    {
        SceneItem item = base.Dispense(overrideLifetime);
        item?.SetKind(SceneItemKind.Coffee);
        return item;
    }
}
