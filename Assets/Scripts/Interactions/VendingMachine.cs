using UnityEngine;
[RequireComponent(typeof(OfficeActionPoint))]

[RequireComponent(typeof(VendingMachineAvatar))]

public class VendingMachine : BaseDispenser
{
    [SerializeField, Min(0f)] private float externalSnackLifetime = 5f;
    [SerializeField, Min(0.01f)] private float handScaleMultiplier = 1f;
    private VendingMachineAvatar avatar;

    private void Awake()
    {
        avatar = GetComponent<VendingMachineAvatar>();
    }

    public SceneItem SpawnSnack() => Dispense();
    public SceneItem SpawnSnackExternal() => Dispense(externalSnackLifetime);
    public SceneItem ConsumeLastSnack() => ConsumeLastItem();

    public Vector3 GetHandScale(SceneItem item)
    {
        if (item == null)
            return Vector3.one;

        Vector3 worldScale = item.transform.lossyScale;
        return worldScale * handScaleMultiplier;
    }

    public override SceneItem Dispense(float? overrideLifetime = null)
    {
        SceneItem item = base.Dispense(overrideLifetime);

        if (item != null && avatar != null)
        {
            float lifetime = overrideLifetime ?? itemLifetime;
            item.CancelScheduledDestroy();
            avatar.PlayDropAnimation(item, lifetime);
        }
        return item;
    }

    public override SceneItem ConsumeLastItem()
    {
        SceneItem item = base.ConsumeLastItem();

        if (avatar != null)
        {
            avatar.CancelAnimation();
        }
        return item;
    }
}
