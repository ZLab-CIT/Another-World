using UnityEngine;

public enum VendingEventScope { OneNearestAgent, NearbyAgents, AllAgents }
public enum VendingRarity { Common, Rare, Epic, Legendary }
public enum UpgradeableFurnitureKind { Plant, CoffeeMachine, Lounge }
public enum InteractionDirection { PhysicalToVirtual, VirtualToPhysical, Bidirectional }
public enum OfflineRewardType { None, DiscountCoupon, FreeItemCoupon, BonusCredit }

public interface IVendingGachaEvent
{
    VendingRarity Rarity { get; }
}

[System.Serializable]
public struct PhysicalInteractionEvent
{
    public string productId;
    public string userId;
}

[System.Serializable]
public struct OfflineCouponReward
{
    public OfflineRewardType rewardType;
    public string couponId;
    public string displayName;
    public string description;
}

/// <summary>Shared data present on every vending event.</summary>
public abstract class VendingEventSO : ScriptableObject
{
    [Header("Identity")]
    public string eventId;
    public string displayName = "Vending Event";
    [TextArea] public string description = "Something happened in the office.";
    public Sprite icon;

    [Header("Targeting")]
    public VendingEventScope scope = VendingEventScope.OneNearestAgent;
    [Tooltip("Radius (world units) used when scope is NearbyAgents.")]
    public float nearbyRadius = 2f;

    [Header("Presentation")]
    [Tooltip("How long the announcement banner is shown before the effect is applied.")]
    public float announceDuration = 1.5f;

    [Header("Physical / Virtual Interaction")]
    public InteractionDirection interactionDirection = InteractionDirection.PhysicalToVirtual;
    [Tooltip("Physical vending-machine product id that can trigger this virtual event.")]
    public string physicalProductId;
    public string targetUserId = "user_001";
    public OfflineCouponReward offlineReward;
}
