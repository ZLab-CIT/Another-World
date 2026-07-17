using UnityEngine;

[CreateAssetMenu(menuName = "Another World/Vending/Confetti Event", fileName = "NewConfettiEvent")]
public class VendingConfettiEventSO : VendingEventSO, IVendingGachaEvent
{
    [Header("Gacha")]
    public VendingRarity rarity = VendingRarity.Common;
    public VendingRarity Rarity => rarity;

    [Header("Confetti")]
    public int count = 24;
    public float duration = 1.2f;
    public Sprite sprite;
}
