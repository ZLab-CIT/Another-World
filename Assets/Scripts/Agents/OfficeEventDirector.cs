using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class OfficeEventDirector : MonoBehaviour
{
    public static OfficeEventDirector Instance { get; private set; }

    [SerializeField, Min(10f)] private float firstCheckDelaySeconds = 8f;
    [SerializeField, Min(20f)] private float checkIntervalSeconds = 45f;
    [SerializeField, Range(1, 5)] private int maxGuests = 4;
    [SerializeField, Range(0f, 1f)] private float giftChance = 0.35f;
    [SerializeField, Range(0f, 1f)] private float hatGiftChance = 0.2f;
    [SerializeField] private HatCatalogSO hatCatalog;

    private readonly List<AIWorkerAgent> workers = new();
    private readonly HashSet<string> completedBirthdayEvents = new();
    private readonly HashSet<string> attemptedBirthdayEvents = new();
    private float nextCheckTime;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        if (hatCatalog == null)
            hatCatalog = Resources.Load<HatCatalogSO>("VendingEvents/HatCatalog");
        nextCheckTime = Time.time + firstCheckDelaySeconds;
    }

    public static OfficeEventDirector Ensure()
    {
        if (Instance != null)
            return Instance;

        return new GameObject(nameof(OfficeEventDirector)).AddComponent<OfficeEventDirector>();
    }

    public void RegisterWorker(AIWorkerAgent worker)
    {
        if (worker != null && !workers.Contains(worker))
            workers.Add(worker);
    }

    public void UnregisterWorker(AIWorkerAgent worker)
    {
        workers.Remove(worker);
    }

    private void Update()
    {
        if (Time.time < nextCheckTime)
            return;

        nextCheckTime = Time.time + checkIntervalSeconds;
        TryStartBirthdayEvent();
    }

    private void TryStartBirthdayEvent()
    {
        AIWorkerAgent birthdayWorker = FindBirthdayWorker();
        if (birthdayWorker == null)
            return;

        string eventKey = DateTime.Today.ToString("yyyy-MM-dd") + ":" + birthdayWorker.AgentId;
        if (completedBirthdayEvents.Contains(eventKey) || attemptedBirthdayEvents.Contains(eventKey))
            return;

        OfficeActionPoint action = FindEventSpot(birthdayWorker);
        if (action == null)
            return;

        List<AIWorkerAgent> guests = SelectGuests(birthdayWorker);
        if (guests.Count == 0)
            return;

        string birthdayTitle = birthdayWorker.DisplayName + "'s Birthday";
        string birthdayDescription = "Today is " + birthdayWorker.DisplayName +
            "'s birthday. Coworkers are gathering to congratulate them.";

        AIWorkerAgent organizer = guests[0];
        BirthdayGift gift = TryPrepareBirthdayGift(birthdayWorker);
        string opener = BuildBirthdayOpener(organizer, birthdayWorker, gift);
        float expiresAt = Time.time + 60f;
        ConversationIntent intent = new()
        {
            initiatorAgentId = organizer.AgentId,
            initiatorName = organizer.DisplayName,
            intendedPartnerName = birthdayWorker.DisplayName,
            topic = birthdayWorker.DisplayName + "'s birthday",
            openingLine = opener,
            generatedByModel = false,
            actionType = action.actionType,
            createdAt = Time.time,
            expiresAt = expiresAt
        };
        intent.onOpeningSpoken = () =>
        {
            if (completedBirthdayEvents.Contains(eventKey))
                return;

            completedBirthdayEvents.Add(eventKey);
            Debug.Log("[Event] " + birthdayDescription, birthdayWorker);
            LLMBrainService.Instance?.RememberWorldEvent(birthdayDescription);
            VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
                birthdayTitle, birthdayDescription, null, 3.5f);
            ApplyBirthdayGift(birthdayWorker, gift);
        };

        birthdayWorker.ReceiveSocialInvitation(action, organizer.DisplayName, expiresAt);
        foreach (AIWorkerAgent guest in guests)
        {
            if (guest == null || guest == organizer)
                continue;
            guest.ReceiveSocialInvitation(action, organizer.DisplayName, expiresAt);
        }

        attemptedBirthdayEvents.Add(eventKey);
        if (!organizer.RequestEventConversation(action, intent))
        {
            attemptedBirthdayEvents.Remove(eventKey);
            return;
        }
        StartCoroutine(ClearAttemptIfUncompleted(eventKey, expiresAt));
    }

    private IEnumerator ClearAttemptIfUncompleted(string eventKey, float expiresAt)
    {
        float waitSeconds = Mathf.Max(0.1f, expiresAt - Time.time + 1f);
        yield return new WaitForSeconds(waitSeconds);
        if (!completedBirthdayEvents.Contains(eventKey))
            attemptedBirthdayEvents.Remove(eventKey);
    }

    private AIWorkerAgent FindBirthdayWorker()
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || string.IsNullOrWhiteSpace(worker.Birthday))
                continue;
            if (worker.TryGetComponent(out AgentConversationController conversation)
                && conversation.IsInConversation)
                continue;
            if (IsToday(worker.Birthday))
                return worker;
        }

        return null;
    }

    private static bool IsToday(string dateText)
    {
        DateTime birthday;
        if (!DateTime.TryParse(dateText, out birthday))
            return false;

        DateTime today = DateTime.Today;
        return birthday.Month == today.Month && birthday.Day == today.Day;
    }

    private List<AIWorkerAgent> SelectGuests(AIWorkerAgent birthdayWorker)
    {
        List<AIWorkerAgent> result = new();
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || worker == birthdayWorker)
                continue;
            if (worker.TryGetComponent(out AgentConversationController conversation)
                && conversation.IsInConversation)
                continue;
            result.Add(worker);
        }

        Shuffle(result);
        while (result.Count > maxGuests)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private OfficeActionPoint FindEventSpot(AIWorkerAgent birthdayWorker)
    {
        OfficeActionPoint[] points = FindObjectsOfType<OfficeActionPoint>();
        OfficeActionPoint best = null;
        float bestScore = float.MinValue;

        foreach (OfficeActionPoint point in points)
        {
            if (point == null || !AgentConversationController.IsSocialSpot(point.actionType))
                continue;
            if (AgentConversationController.IsConversationActiveAt(point))
                continue;
            if (point.IsReservedByOther(birthdayWorker))
                continue;

            float score = point.actionType == OfficeActionType.BreakSpot ? 20f : 10f;
            score -= Vector2.Distance(birthdayWorker.GetPosition(), point.transform.position);
            if (score > bestScore)
            {
                bestScore = score;
                best = point;
            }
        }

        return best;
    }

    private BirthdayGift TryPrepareBirthdayGift(AIWorkerAgent birthdayWorker)
    {
        if (birthdayWorker == null || UnityEngine.Random.value >= giftChance)
            return BirthdayGift.None;

        if (UnityEngine.Random.value < hatGiftChance)
        {
            HatCatalogSO.HatEntry hat = hatCatalog != null
                ? hatCatalog.PickRandomHat(birthdayWorker.AgentType)
                : null;
            HatCatalogSO.HatPool pool = hatCatalog != null
                ? hatCatalog.GetPool(birthdayWorker.AgentType)
                : null;
            if (hat != null && pool != null)
                return BirthdayGift.Hat(hat, pool);
        }

        return BirthdayGift.SmallSnack;
    }

    private void ApplyBirthdayGift(AIWorkerAgent birthdayWorker, BirthdayGift gift)
    {
        if (birthdayWorker == null || gift.kind != BirthdayGiftKind.Hat
            || gift.hat == null || gift.pool == null)
            return;

        AgentCosmetics cosmetics = birthdayWorker.GetComponent<AgentCosmetics>();
        if (cosmetics == null)
            cosmetics = birthdayWorker.gameObject.AddComponent<AgentCosmetics>();
        cosmetics.ApplyHat(gift.hat.sprite, gift.pool, gift.hat.localScale);

        VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
            "Birthday Gift", birthdayWorker.DisplayName + " got a new hat.", gift.hat.sprite, 2.5f);
        LLMBrainService.Instance?.RememberWorldEvent(
            birthdayWorker.DisplayName + " received a hat as a birthday gift.");
    }

    private string BuildBirthdayOpener(AIWorkerAgent organizer, AIWorkerAgent birthdayWorker,
        BirthdayGift gift)
    {
        string giftLine = "";
        switch (gift.kind)
        {
            case BirthdayGiftKind.Hat:
                giftLine = " We brought you a hat too.";
                break;
            case BirthdayGiftKind.SmallSnack:
                giftLine = " I brought you a small snack too.";
                break;
        }

        return birthdayWorker.DisplayName + ", happy birthday. We did not want the day to pass quietly." + giftLine;
    }

    private static void Shuffle<T>(List<T> values)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private enum BirthdayGiftKind
    {
        None,
        SmallSnack,
        Hat
    }

    private struct BirthdayGift
    {
        public BirthdayGiftKind kind;
        public HatCatalogSO.HatEntry hat;
        public HatCatalogSO.HatPool pool;

        public static BirthdayGift None => new() { kind = BirthdayGiftKind.None };
        public static BirthdayGift SmallSnack => new() { kind = BirthdayGiftKind.SmallSnack };

        public static BirthdayGift Hat(HatCatalogSO.HatEntry hat, HatCatalogSO.HatPool pool)
        {
            return new BirthdayGift
            {
                kind = BirthdayGiftKind.Hat,
                hat = hat,
                pool = pool
            };
        }
    }
}
