using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(VendingEventDispatcher))]
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
    private readonly HashSet<string> processedPhysicalEventIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> processedPhysicalEventOrder = new();
    private VendingEventDispatcher dispatcher;
    public event Action<PhysicalInteractionEvent, VendingEventSO> PhysicalEventAccepted;
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
        dispatcher = GetComponent<VendingEventDispatcher>();
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
        IssueCoupon(resolvedUserId, new OfflineCouponReward
        {
            rewardType = OfflineRewardType.DiscountCoupon,
            couponId = defaultCouponId,
            displayName = defaultCouponName,
            description = defaultCouponDescription
        }, "mock-productivity-" + DateTime.Now.ToString("yyyy-MM-dd"));
    }

    public void TriggerMockOnlineMilestone()
    {
        TriggerMockOnlineMilestone(defaultUserId);
    }

    public void TriggerPhysicalSale(string productId, string userId)
    {
        PhysicalInteractionEvent physicalEvent = new()
        {
            eventId = Guid.NewGuid().ToString("N"),
            productId = productId,
            userId = string.IsNullOrEmpty(userId) ? defaultUserId : userId,
            occurredAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            source = "local"
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

        VendingEventSO evt = dispatcher.FindEventByProductId(physicalEvent.productId);
        if (evt == null)
        {
            LogHistory("physical_sale rejected: no virtual event for product '" + physicalEvent.productId + "'");
            Debug.LogWarning(nameof(PhysicalVirtualInteractionBridge) + ": No event mapped for product '" + physicalEvent.productId + "'.");
            return;
        }

        if (string.IsNullOrWhiteSpace(physicalEvent.eventId))
            physicalEvent.eventId = Guid.NewGuid().ToString("N");
        if (!processedPhysicalEventIds.Add(physicalEvent.eventId))
        {
            LogHistory("physical_sale ignored: duplicate event '" +
                physicalEvent.eventId + "'");
            return;
        }
        processedPhysicalEventOrder.Enqueue(physicalEvent.eventId);
        while (processedPhysicalEventOrder.Count > 256)
            processedPhysicalEventIds.Remove(processedPhysicalEventOrder.Dequeue());

        LogHistory("physical_sale: " + physicalEvent.productId + " -> virtual_event: "
            + evt.eventId + " user: " + physicalEvent.userId + " event: "
            + physicalEvent.eventId);
        dispatcher.TriggerEvent(evt);
        PhysicalEventAccepted?.Invoke(physicalEvent, evt);
        OfficeInteractionHubClient.Instance?.RecordWorldEvent(
            "physical_vending", evt.displayName,
            evt.description, Array.Empty<string>());

        if (evt.interactionDirection == InteractionDirection.Bidirectional)
            IssueCouponFromEvent(physicalEvent.userId, evt, physicalEvent.eventId);
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
        IssueCoupon(userId, reward, "unity-" + Guid.NewGuid().ToString("N"));
    }

    private void IssueCoupon(string userId, OfflineCouponReward reward,
        string sourceEventId)
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

        LogHistory("coupon_requested: " + reward.couponId + " -> user: " + resolvedUserId);
        OfficeInteractionHubClient.Ensure().PublishClaimableReward(
            sourceEventId, reward.rewardType.ToString(), reward.displayName,
            reward.description, 300);
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

        string milestoneId = "avg_productivity_"
            + Mathf.RoundToInt(productivityCouponThreshold) + "_"
            + DateTime.Now.ToString("yyyy-MM-dd");
        if (issueMilestoneCouponOnlyOnce && issuedMilestones.Contains(milestoneId))
            return;

        issuedMilestones.Add(milestoneId);
        LogHistory("virtual_milestone: average productivity " + average.ToString("0.0"));
        IssueCoupon(defaultUserId, new OfflineCouponReward
        {
            rewardType = OfflineRewardType.DiscountCoupon,
            couponId = defaultCouponId,
            displayName = defaultCouponName,
            description = defaultCouponDescription
        }, milestoneId);
    }

    private void IssueCouponFromEvent(string userId, VendingEventSO evt,
        string sourceEventId)
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

        IssueCoupon(string.IsNullOrEmpty(userId) ? evt.targetUserId : userId,
            reward, "vending-" + sourceEventId);
    }

    private string PickProduct(bool cosmetic)
    {
        VendingEventSO evt = dispatcher.PickEvent(cosmetic);
        return evt != null ? evt.physicalProductId : "";
    }

    private void LogHistory(string line)
    {
        Debug.Log(nameof(PhysicalVirtualInteractionBridge) + ": " + line);
    }
}
