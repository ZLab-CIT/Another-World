using UnityEngine;

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
    None,
    AgentTint,
    FurnitureUnlock,
    ConfettiBurst,
    AgentHat
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
    [Tooltip("Color applied to the agent sprite (AgentTint) or to the placeholder furniture.")]
    public Color agentTint = Color.white;
    [Tooltip("Optional prefab (with an OfficeActionPoint) for FurnitureUnlock. If empty, a placeholder is spawned.")]
    public GameObject furniturePrefab;
    [Tooltip("Number of confetti pieces for ConfettiBurst.")]
    public int confettiCount = 24;
    public float confettiDuration = 1.2f;
    [Tooltip("Sprite for each confetti piece. Leave empty to use a colored square.")]
    public Sprite confettiSprite;

    [Header("Presentation")]
    [Tooltip("How long the announcement banner is shown before the effect is applied.")]
    public float announceDuration = 1.5f;
}
