using System.Collections.Generic;
using UnityEngine;

public class PhysicalVirtualInteractionBridge : MonoBehaviour
{
    public static PhysicalVirtualInteractionBridge Instance { get; private set; }

    [System.Serializable]
    public class ProductEventBinding
    {
        public string productId;
        public string eventId;
        public string displayName;
    }

    [Header("Prototype Users")]
    [SerializeField] private string defaultUserId = "user_001";
    [SerializeField] private string defaultMachineId = "office_vending_01";

    [Header("Physical Product Mapping")]
    [SerializeField] private List<ProductEventBinding> productBindings = new List<ProductEventBinding>();

    [Header("Coupon Rules")]
    [SerializeField] private string defaultCouponId = "NEXT_DRINK_20";
    [SerializeField] private string defaultCouponName = "20% Next Drink";
    [SerializeField] private string defaultCouponDescription = "Offline vending coupon issued from an online office event.";
    [SerializeField] private float productivityCouponThreshold = 45f;
    [SerializeField] private bool issueMilestoneCouponOnlyOnce = true;

    private readonly List<string> eventHistory = new List<string>();
    private readonly HashSet<string> issuedMilestones = new HashSet<string>();
    private readonly string[] defaultBuffProducts = { "coffee", "energy_drink", "water", "snack", "vending_treat", "lunch" };
    private readonly string[] defaultGachaProducts =
    {
        "confetti_pack",
        "disco_pass",
        "blue_skin_token",
        "office_plant",
        "lounge_upgrade",
        "gold_skin_token",
        "surprise_box"
    };

    public IReadOnlyList<string> EventHistory => eventHistory;

    public static PhysicalVirtualInteractionBridge Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new GameObject(nameof(PhysicalVirtualInteractionBridge));
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

        if (productBindings == null)
            productBindings = new List<ProductEventBinding>();

        if (productBindings.Count == 0)
            AddDefaultProductBindings();

        VendingEventDispatcher.Ensure();
    }

    public void TriggerMockPhysicalSale(string productId)
    {
        TriggerPhysicalSale(productId, defaultUserId);
    }

    public void TriggerMockPhysicalSale()
    {
        TriggerMockPhysicalSale(PickProduct(defaultBuffProducts));
    }

    public void TriggerMockGachaSale()
    {
        TriggerPhysicalSale(PickProduct(defaultGachaProducts), defaultUserId);
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
        PhysicalInteractionEvent physicalEvent = new PhysicalInteractionEvent
        {
            eventType = PhysicalEventType.Sale,
            productId = productId,
            userId = string.IsNullOrEmpty(userId) ? defaultUserId : userId,
            sourceId = defaultMachineId
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
        OfflineCouponReward reward = new OfflineCouponReward
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

        string eventId = ResolveEventId(productId);
        return dispatcher.FindEventById(eventId);
    }

    private string ResolveEventId(string productId)
    {
        foreach (ProductEventBinding binding in productBindings)
        {
            if (binding != null && binding.productId == productId)
                return binding.eventId;
        }

        return productId;
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

    private void AddDefaultProductBindings()
    {
        productBindings.Add(NewBinding("coffee", "coffee", "Coffee"));
        productBindings.Add(NewBinding("energy_drink", "sugar_rush", "Energy Drink"));
        productBindings.Add(NewBinding("water", "hydration", "Water"));
        productBindings.Add(NewBinding("snack", "snack", "Snack"));
        productBindings.Add(NewBinding("vending_treat", "vending_drop", "Vending Treat"));
        productBindings.Add(NewBinding("lunch", "healthy_lunch", "Healthy Lunch"));
        productBindings.Add(NewBinding("confetti_pack", "confetti", "Confetti Pack"));
        productBindings.Add(NewBinding("disco_pass", "disco", "Disco Pass"));
        productBindings.Add(NewBinding("blue_skin_token", "tint_blue", "Blue Skin Token"));
        productBindings.Add(NewBinding("office_plant", "plant", "Office Plant"));
        productBindings.Add(NewBinding("lounge_upgrade", "lounge", "Lounge Upgrade"));
        productBindings.Add(NewBinding("gold_skin_token", "tint_gold", "Gold Skin Token"));
        productBindings.Add(NewBinding("surprise_box", "hat", "Surprise Box"));
    }

    private static string PickProduct(string[] products)
    {
        if (products == null || products.Length == 0)
            return "";

        return products[Random.Range(0, products.Length)];
    }

    private static ProductEventBinding NewBinding(string productId, string eventId, string displayName)
    {
        return new ProductEventBinding
        {
            productId = productId,
            eventId = eventId,
            displayName = displayName
        };
    }

    private void LogHistory(string line)
    {
        eventHistory.Add(Time.time.ToString("0.0") + "s " + line);
        Debug.Log(nameof(PhysicalVirtualInteractionBridge) + ": " + line);
    }
}
