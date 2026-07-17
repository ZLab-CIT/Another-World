using UnityEngine;

[CreateAssetMenu(menuName = "Another World/Vending/Hat Event", fileName = "NewHatEvent")]
public class VendingHatEventSO : VendingEventSO, IVendingGachaEvent
{
    public VendingRarity rarity = VendingRarity.Common;
    public VendingRarity Rarity => rarity;
}
