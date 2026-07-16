using System.Collections.Generic;
using UnityEngine;

public class PhysicalVirtualInteractionBridge : MonoBehaviour
{
    public static PhysicalVirtualInteractionBridge Instance { get; private set; }

    [Header("Prototype Users")]
    [SerializeField] private string defaultUserId = "user_001";

    [Header("Coupon Rules")]
    [SerializeField] private string defaultCouponId = "NEXT_DRINK_20";
    [SerializeField] private string defaultCouponName = "20% Next Drink";
    [SerializeField] private string defaultCouponDescription = "Offline vending coupon issued from an online office event.";
    [SerializeField] private float productivityCouponThreshold = 45f;
    [SerializeField] private bool issueMilestoneCouponOnlyOnce = true;

    private readonly HashSet<string> issuedMilestones = new();
    public static PhysicalVirtualInteractionBridge Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new(nameof(PhysicalVirtualInteractionBridge));
        return go.AddComponent<PhysicalVirtualInteractionBridge>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        VendingEventDispatcher.Ensure();
    }

    public void TriggerMockPhysicalSale(string productId)
    {
        TriggerPhysicalSale(productId, defaultUserId);
    }

    public void TriggerMockPhysicalSale()
    {
        TriggerMockPhysicalSale(PickProduct(cosmetic: false));
    }

    public void TriggerMockGachaSale()
    {
        TriggerPhysicalSale(PickProduct(cosmetic: true), defaultUserId);
    }

    public void TriggerMockOnlineMilestone(string userId)
    {
        string resolvedUserId = string.IsNullOrEmpty(userId) ? defaultUserId : userId;
        IssueCoupon(resolvedUserId, defaultCouponId);
    }

    public void TriggerMockOnlineMilestone()
    {
        TriggerMockOnlineMilestone(defaultUserId);
    }

    public void TriggerPhysicalSale(string productId, string userId)
    {
        PhysicalInteractionEvent physicalEvent = new()
        {
            productId = productId,
            userId = string.IsNullOrEmpty(userId) ? defaultUserId : userId
        };

        HandlePhysicalEvent(physicalEvent);
    }

    public void HandlePhysicalEvent(PhysicalInteractionEvent physicalEvent)
    {
        if (string.IsNullOrEmpty(physicalEvent.productId))
        {
            LogHistory("physical_sale rejected: empty product id");
            return;
        }

        VendingEventDispatcher dispatcher = VendingEventDispatcher.Instance;
        if (dispatcher == null)
            dispatcher = VendingEventDispatcher.Ensure();

        VendingEventSO evt = FindEventForProduct(dispatcher, physicalEvent.productId);
        if (evt == null)
        {
            LogHistory("physical_sale rejected: no virtual event for product '" + physicalEvent.productId + "'");
            Debug.LogWarning(nameof(PhysicalVirtualInteractionBridge) + ": No event mapped for product '" + physicalEvent.productId + "'.");
            return;
        }

        LogHistory("physical_sale: " + physicalEvent.productId + " -> virtual_event: " + evt.eventId + " user: " + physicalEvent.userId);
        dispatcher.TriggerEvent(evt);

        if (evt.interactionDirection == InteractionDirection.Bidirectional)
            IssueCouponFromEvent(physicalEvent.userId, evt);
    }

    public void IssueCoupon(string userId, string couponId)
    {
        OfflineCouponReward reward = new()
        {
            rewardType = OfflineRewardType.DiscountCoupon,
            couponId = string.IsNullOrEmpty(couponId) ? defaultCouponId : couponId,
            displayName = defaultCouponName,
            description = defaultCouponDescription
        };

        IssueCoupon(userId, reward);
    }

    public void IssueCoupon(string userId, OfflineCouponReward reward)
    {
        string resolvedUserId = string.IsNullOrEmpty(userId) ? defaultUserId : userId;
        if (string.IsNullOrEmpty(reward.couponId))
            reward.couponId = defaultCouponId;
        if (string.IsNullOrEmpty(reward.displayName))
            reward.displayName = defaultCouponName;
        if (string.IsNullOrEmpty(reward.description))
            reward.description = defaultCouponDescription;
        if (reward.rewardType == OfflineRewardType.None)
            reward.rewardType = OfflineRewardType.DiscountCoupon;

        LogHistory("coupon_issued: " + reward.couponId + " -> user: " + resolvedUserId);

        VendingEventDispatcher dispatcher = VendingEventDispatcher.Instance;
        if (dispatcher == null)
            dispatcher = VendingEventDispatcher.Ensure();

        dispatcher.ShowOfflineCoupon(resolvedUserId, reward);
    }

    public void EvaluateProductivityMilestone()
    {
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null || crowd.Workers.Count == 0)
            return;

        float total = 0f;
        int count = 0;
        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker == null)
                continue;

            total += worker.productivity;
            count++;
        }

        if (count == 0)
            return;

        float average = total / count;
        if (average < productivityCouponThreshold)
            return;

        string milestoneId = "avg_productivity_" + Mathf.RoundToInt(productivityCouponThreshold);
        if (issueMilestoneCouponOnlyOnce && issuedMilestones.Contains(milestoneId))
            return;

        issuedMilestones.Add(milestoneId);
        LogHistory("virtual_milestone: average productivity " + average.ToString("0.0"));
        IssueCoupon(defaultUserId, defaultCouponId);
    }

    private VendingEventSO FindEventForProduct(VendingEventDispatcher dispatcher, string productId)
    {
        if (dispatcher == null)
            return null;

        foreach (VendingEventSO evt in dispatcher.Events)
        {
            if (evt == null)
                continue;

            if (!string.IsNullOrEmpty(evt.physicalProductId) && evt.physicalProductId == productId)
                return evt;
        }

        return null;
    }

    private void IssueCouponFromEvent(string userId, VendingEventSO evt)
    {
        OfflineCouponReward reward = evt.offlineReward;
        if (string.IsNullOrEmpty(reward.couponId))
            reward.couponId = defaultCouponId;
        if (string.IsNullOrEmpty(reward.displayName))
            reward.displayName = evt.displayName + " Coupon";
        if (string.IsNullOrEmpty(reward.description))
            reward.description = "Reward issued after " + evt.displayName + ".";
        if (reward.rewardType == OfflineRewardType.None)
            reward.rewardType = OfflineRewardType.DiscountCoupon;

        IssueCoupon(string.IsNullOrEmpty(userId) ? evt.targetUserId : userId, reward);
    }

    private static string PickProduct(bool cosmetic)
    {
        VendingEventDispatcher dispatcher = VendingEventDispatcher.Instance ?? VendingEventDispatcher.Ensure();
        List<string> products = new();
        foreach (VendingEventSO evt in dispatcher.Events)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.physicalProductId))
                continue;

            if ((evt.cosmeticType != VendingCosmeticType.None) == cosmetic)
                products.Add(evt.physicalProductId);
        }

        if (products.Count == 0)
            return "";

        return products[Random.Range(0, products.Count)];
    }

    private void LogHistory(string line)
    {
        Debug.Log(nameof(PhysicalVirtualInteractionBridge) + ": " + line);
    }
}
