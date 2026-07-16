using UnityEngine;
using UnityEngine.Serialization;

public enum VendingEventScope
{
    OneNearestAgent,
    NearbyAgents,
    AllAgents
}

public enum VendingRarity
{
    Common,
    Rare,
    Epic,
    Legendary
}

public enum VendingCosmeticType
{
    None = 0,
    FurnitureUnlock = 2,
    ConfettiBurst = 3,
    AgentHat = 4,
    DiscoDance = 5
}

public enum UpgradeableFurnitureKind
{
    Plant,
    CoffeeMachine,
    Lounge
}

public enum InteractionDirection
{
    PhysicalToVirtual,
    VirtualToPhysical,
    Bidirectional
}

public enum OfflineRewardType
{
    None,
    DiscountCoupon,
    FreeItemCoupon,
    BonusCredit
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

[CreateAssetMenu(menuName = "ZhipuOffice/Vending Event", fileName = "NewVendingEvent")]
public class VendingEventSO : ScriptableObject
{
    [Header("Identity")]
    public string eventId;
    public string displayName = "Vending Event";
    [TextArea] public string description = "Something happened in the office.";
    public Sprite icon;

    [Header("World Drop")]
    public Sprite dropSprite;
    [Tooltip("Optional random pool for temporary vending drops. If not empty, one sprite is selected per event.")]
    public Sprite[] dropSprites;
    public float dropLifetime = 3f;
    public float dropScale = 1f;

    [Header("Targeting")]
    public VendingEventScope scope = VendingEventScope.OneNearestAgent;
    [Tooltip("Radius (world units) used when scope is NearbyAgents.")]
    public float nearbyRadius = 2f;

    [Header("Instant Need Effects")]
    public float energyChange = 0f;
    public float focusChange = 0f;
    public float socialChange = 0f;
    public float productivityChange = 0f;

    [Header("Temporary Speed Buff")]
    [Tooltip("Multiplier applied to movement speed. 1 = no change.")]
    public float speedMultiplier = 1f;
    [Tooltip("How long the speed buff lasts, in seconds. 0 = no buff.")]
    public float speedBuffDuration = 0f;

    [Header("Temporary Decay Override")]
    [Tooltip("Multiplier applied to need decay while active. 1 = unchanged, 0 = frozen.")]
    public float energyDecayMultiplier = 1f;
    public float focusDecayMultiplier = 1f;
    public float socialDecayMultiplier = 1f;
    [Tooltip("How long the decay override lasts, in seconds. 0 = no override.")]
    public float decayOverrideDuration = 0f;

    [Header("Post-Buff Crash (applied after speedBuffDuration expires)")]
    public float postEnergyChange = 0f;
    public float postFocusChange = 0f;
    public float postSocialChange = 0f;
    public float postProductivityChange = 0f;

    [Header("Gacha / Cosmetic")]
    public VendingRarity rarity = VendingRarity.Common;
    public VendingCosmeticType cosmeticType = VendingCosmeticType.None;
    [FormerlySerializedAs("agentTint")]
    [Tooltip("Color used when FurnitureUnlock has no sprite or prefab.")]
    public Color placeholderColor = Color.white;
    [Tooltip("Optional prefab (with an OfficeActionPoint) for FurnitureUnlock. If empty, a placeholder is spawned.")]
    public GameObject furniturePrefab;
    [Tooltip("Optional random pool for decoration/furniture sprites.")]
    public Sprite[] furnitureSprites;
    public UpgradeableFurnitureKind furnitureKind = UpgradeableFurnitureKind.Plant;
    [Tooltip("Named socket used for deterministic placement, e.g. PlantCorner or LoungeUpgrade.")]
    public string furnitureSocketId;
    [Tooltip("Number of confetti pieces for ConfettiBurst.")]
    public int confettiCount = 24;
    public float confettiDuration = 1.2f;
    [Tooltip("Sprite for each confetti piece. Leave empty to use a colored square.")]
    public Sprite confettiSprite;

    [Header("Presentation")]
    [Tooltip("How long the announcement banner is shown before the effect is applied.")]
    public float announceDuration = 1.5f;

    [Header("Physical / Virtual Interaction")]
    public InteractionDirection interactionDirection = InteractionDirection.PhysicalToVirtual;
    [Tooltip("Physical vending-machine product id that can trigger this virtual event.")]
    public string physicalProductId;
    [Tooltip("Default offline user used by prototype rewards if no user id is supplied.")]
    public string targetUserId = "user_001";
    [Tooltip("Coupon or reward produced when this event completes a bidirectional loop.")]
    public OfflineCouponReward offlineReward;
}
