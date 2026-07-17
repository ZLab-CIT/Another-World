using UnityEngine;

[CreateAssetMenu(menuName = "Another World/Vending/Buff Event", fileName = "NewBuffEvent")]
public class VendingBuffEventSO : VendingEventSO
{
    [Header("World Drop")]
    public Sprite dropSprite;
    public Sprite[] dropSprites;
    public float dropLifetime = 3f;
    public float dropScale = 1f;

    [Header("Instant Need Effects")]
    public float energyChange;
    public float focusChange;
    public float socialChange;
    public float productivityChange;

    [Header("Temporary Speed Buff")]
    public float speedMultiplier = 1f;
    public float speedBuffDuration;

    [Header("Temporary Decay Override")]
    public float energyDecayMultiplier = 1f;
    public float focusDecayMultiplier = 1f;
    public float socialDecayMultiplier = 1f;
    public float decayOverrideDuration;

    [Header("Post-Buff Crash")]
    public float postEnergyChange;
    public float postFocusChange;
    public float postSocialChange;
    public float postProductivityChange;
}
