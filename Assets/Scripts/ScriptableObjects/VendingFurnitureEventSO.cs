using UnityEngine;

[CreateAssetMenu(menuName = "Another World/Vending/Furniture Event", fileName = "NewFurnitureEvent")]
public class VendingFurnitureEventSO : VendingEventSO, IVendingGachaEvent
{
    [Header("Gacha")]
    public VendingRarity rarity = VendingRarity.Common;
    public VendingRarity Rarity => rarity;

    [Header("Furniture")]
    public Color placeholderColor = Color.white;
    public GameObject furniturePrefab;
    public Sprite[] furnitureSprites;
    [Min(0.01f)] public float furnitureScale = 1f;
    public UpgradeableFurnitureKind furnitureKind = UpgradeableFurnitureKind.Plant;
    public string furnitureSocketId;
}
