using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class OfficeEventDirector : MonoBehaviour
{
    private sealed class PendingStoryFollowUp
    {
        public OfficeStoryBeatSO story;
        public AIWorkerAgent speaker;
        public AIWorkerAgent target;
        public PersistedStoryArcState arc;
        public string generatedMemory;
        public bool inFlight;
        public float executeAt;
        public float expiresAt;
    }

    public static OfficeEventDirector Instance { get; private set; }

    [SerializeField, Min(10f)] private float firstCheckDelaySeconds = 8f;
    [SerializeField, Min(20f)] private float checkIntervalSeconds = 45f;
    [SerializeField, Range(1, 5)] private int maxGuests = 4;
    [SerializeField, Range(0f, 1f)] private float giftChance = 0.35f;
    [SerializeField, Range(0f, 1f)] private float hatGiftChance = 0.2f;
    [SerializeField] private HatCatalogSO hatCatalog;

    [Header("Ambient Office Stories")]
    [SerializeField] private bool enableAmbientStories = true;
    [SerializeField, Min(5f)] private float firstAmbientStoryDelaySeconds = 20f;
    [SerializeField, Min(20f)] private float minimumAmbientStoryIntervalSeconds = 180f;
    [SerializeField, Min(20f)] private float maximumAmbientStoryIntervalSeconds = 300f;
    [SerializeField] private bool announceAllAmbientStories = true;
    [SerializeField, Min(0f)] private float vendingConversationCooldownSeconds = 300f;

    private readonly List<AIWorkerAgent> workers = new();
    private readonly HashSet<string> completedBirthdayEvents = new();
    private readonly HashSet<string> attemptedBirthdayEvents = new();
    private readonly HashSet<string> completedBirthdayPreparations = new();
    private readonly Queue<string> recentStoryIds = new();
    private readonly List<PendingStoryFollowUp> pendingStoryFollowUps = new();
    private readonly HashSet<string> queuedArcIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> nextVendingConversationTimes =
        new(StringComparer.OrdinalIgnoreCase);
    private OfficeStoryBeatSO[] ambientStories;
    private float nextCheckTime;
    private float nextAmbientStoryTime;
    private bool ambientStoryInFlight;

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
        ambientStories = Resources.LoadAll<OfficeStoryBeatSO>("OfficeStories");
        nextCheckTime = Time.time + firstCheckDelaySeconds;
        nextAmbientStoryTime = Time.time + firstAmbientStoryDelaySeconds;
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
        RestorePersistedStoryArcs();
    }

    public void UnregisterWorker(AIWorkerAgent worker)
    {
        workers.Remove(worker);
    }

    public void NotifyBirthdayPreparationCompleted(AIWorkerAgent creator, Vector2 location,
        string description)
    {
        if (creator == null || !IsBirthdayPreparation(description))
            return;

        AIWorkerAgent birthdayWorker = FindBirthdayWorker(false);
        if (birthdayWorker == null || birthdayWorker == creator)
            return;

        string key = DateTime.Today.ToString("yyyy-MM-dd") + ":" + creator.AgentId + ":"
            + (description ?? "").Trim().ToLowerInvariant();
        if (!completedBirthdayPreparations.Add(key))
            return;

        string worldEvent = creator.DisplayName + " finished preparing a birthday surprise for "
            + birthdayWorker.DisplayName + ".";
        Debug.Log("[Birthday preparation completed] " + worldEvent, creator);
        LLMBrainService.Instance?.RememberWorldEvent(worldEvent);
        birthdayWorker.ReceivePreparedBirthdaySurprise(creator, location, description);
    }

    public async void NotifyVendingEvent(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        if (evt == null)
            return;

        AIWorkerAgent reactor = FirstWorker(targets);
        if (reactor == null)
            reactor = FirstAvailable(workers);
        if (reactor == null)
            return;

        reactor.ReactToWorldEvent(BuildVendingReaction(evt));
        string eventKey = !string.IsNullOrWhiteSpace(evt.eventId)
            ? evt.eventId : evt.displayName;
        if (!string.IsNullOrWhiteSpace(eventKey)
            && nextVendingConversationTimes.TryGetValue(eventKey, out float nextAllowed)
            && Time.unscaledTime < nextAllowed)
            return;
        if (!string.IsNullOrWhiteSpace(eventKey))
            nextVendingConversationTimes[eventKey] =
                Time.unscaledTime + vendingConversationCooldownSeconds;

        AIWorkerAgent partner = FindStoryPartner(reactor, targets);
        if (partner == null)
            partner = FindStoryPartner(reactor, workers);
        if (partner != null)
        {
            string topic = evt.displayName + ": " + evt.description;
            PreparedStoryConversation generated = null;
            LLMBrainService brain = LLMBrainService.Instance;
            if (brain != null
                && reactor.TryGetComponent(out AgentConversationController reactorConversation)
                && partner.TryGetComponent(out AgentConversationController partnerConversation))
            {
                generated = await brain.GenerateStoryConversationAsync(
                    reactorConversation.BuildParticipantContext(),
                    partnerConversation.BuildParticipantContext(),
                    evt.displayName, topic, 0, "A physical purchase entered the world.");
            }
            if (generated == null)
                return;
            string opening = generated.openingLine;
            reactor.RequestApproachConversation(partner,
                !string.IsNullOrWhiteSpace(generated.topic)
                    ? generated.topic : topic,
                opening, highPriority: true, preparedScript: generated.continuation);
            if (!string.IsNullOrWhiteSpace(generated.memory))
                brain?.RememberWorldEvent(generated.memory);
        }
    }

    private void Update()
    {
        TryStartDueFollowUps();

        if (Time.time >= nextCheckTime)
        {
            nextCheckTime = Time.time + checkIntervalSeconds;
            TryStartBirthdayEvent();
        }

        if (enableAmbientStories && Time.time >= nextAmbientStoryTime)
        {
            nextAmbientStoryTime = float.MaxValue;
            BeginAmbientStory();
        }
    }

    private async void BeginAmbientStory()
    {
        if (ambientStoryInFlight)
            return;
        ambientStoryInFlight = true;
        bool started = false;
        try
        {
            started = await TryStartAmbientStoryAsync();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Ambient story failed: " + exception.Message, this);
        }
        finally
        {
            ambientStoryInFlight = false;
            ScheduleNextAmbientStory(started);
        }
    }

    private async Task<bool> TryStartAmbientStoryAsync()
    {
        if (ambientStories == null || ambientStories.Length == 0)
            return false;

        List<AIWorkerAgent> available = new();
        foreach (AIWorkerAgent worker in workers)
            if (worker != null && worker.CanJoinStoryBeat)
                available.Add(worker);
        if (available.Count < 2)
            return false;

        OfficeStoryBeatSO story = PickStory();
        if (story == null)
            return false;

        AIWorkerAgent speaker = PickStorySpeaker(available, story.topic);
        available.Remove(speaker);
        AIWorkerAgent target = available[UnityEngine.Random.Range(0, available.Count)];
        string topic = Expand(story.topic, speaker, target);
        string establishedFact = Expand(story.memory, speaker, target);
        PreparedStoryConversation generated = await GenerateStoryScene(
            speaker, target, story, establishedFact, 0, "");
        if (generated == null)
        {
            speaker.ReactToWorldEvent(topic);
            target.ReactToWorldEvent(topic);
            RememberStory(story, speaker, target, establishedFact);
            RememberRecentStory(story.storyId);
            ShowAmbientAnnouncement(story, establishedFact);
            return true;
        }
        string opening = generated.openingLine;
        string resolvedTopic = !string.IsNullOrWhiteSpace(generated.topic)
            ? generated.topic : topic;
        bool conversationStarted = speaker.RequestApproachConversation(
            target, resolvedTopic, opening,
            preparedScript: generated?.continuation);

        UpdateStoryVisual(story, 0);
        string memory = !string.IsNullOrWhiteSpace(generated.memory)
            ? generated.memory : establishedFact;
        if (!conversationStarted)
        {
            speaker.ReactToWorldEvent(resolvedTopic);
            target.ReactToWorldEvent(resolvedTopic);
        }
        RememberStory(story, speaker, target, memory);
        ScheduleFollowUp(story, speaker, target, memory);
        RememberRecentStory(story.storyId);
        ShowAmbientAnnouncement(story, memory);
        return true;
    }

    private void ShowAmbientAnnouncement(OfficeStoryBeatSO story, string memory)
    {
        if (story == null
            || (!announceAllAmbientStories && !story.publicAnnouncement))
            return;
        VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
            story.title, memory, null, 3.5f);
    }

    private void ScheduleFollowUp(OfficeStoryBeatSO story, AIWorkerAgent speaker,
        AIWorkerAgent target, string establishedFact)
    {
        if (story == null)
            return;

        float delay = story.followUpDelaySeconds > 0f
            ? story.followUpDelaySeconds : UnityEngine.Random.Range(75f, 130f);
        LLMBrainService brain = LLMBrainService.Instance;
        PersistedStoryArcState arc = new()
        {
            arcId = Guid.NewGuid().ToString("N"),
            storyId = story.storyId,
            speakerAgentId = speaker.AgentId,
            targetAgentId = target.AgentId,
            establishedFact = establishedFact,
            stage = 1,
            nextStageWorldTime = (brain?.WorldUnixSeconds ?? 0d)
                + (brain?.WorldSecondsFromRealSeconds(delay) ?? delay),
            status = "active"
        };
        brain?.UpsertStoryArc(arc);
        queuedArcIds.Add(arc.arcId);
        float executeAt = Time.time + Mathf.Max(10f, delay);
        pendingStoryFollowUps.Add(new PendingStoryFollowUp
        {
            story = story,
            speaker = speaker,
            target = target,
            arc = arc,
            executeAt = executeAt,
            expiresAt = executeAt + 180f
        });
    }

    private void RestorePersistedStoryArcs()
    {
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || ambientStories == null)
            return;

        foreach (PersistedStoryArcState arc in brain.StoryArcs)
        {
            if (arc == null || !string.Equals(arc.status, "active",
                    StringComparison.OrdinalIgnoreCase)
                || !queuedArcIds.Add(arc.arcId))
                continue;

            AIWorkerAgent speaker = FindWorker(arc.speakerAgentId);
            AIWorkerAgent target = FindWorker(arc.targetAgentId);
            OfficeStoryBeatSO story = Array.Find(ambientStories, candidate =>
                candidate != null && string.Equals(candidate.storyId, arc.storyId,
                    StringComparison.OrdinalIgnoreCase));
            if (speaker == null || target == null || story == null)
            {
                queuedArcIds.Remove(arc.arcId);
                continue;
            }

            float delay = Mathf.Max(2f, brain.RealSecondsUntil(arc.nextStageWorldTime));
            pendingStoryFollowUps.Add(new PendingStoryFollowUp
            {
                story = story,
                speaker = speaker,
                target = target,
                arc = arc,
                executeAt = Time.time + delay,
                expiresAt = Time.time + delay + 240f
            });
        }
    }

    private AIWorkerAgent FindWorker(string agentId)
    {
        foreach (AIWorkerAgent worker in workers)
            if (worker != null && string.Equals(worker.AgentId, agentId,
                    StringComparison.OrdinalIgnoreCase))
                return worker;
        return null;
    }

    private void TryStartDueFollowUps()
    {
        for (int i = pendingStoryFollowUps.Count - 1; i >= 0; i--)
        {
            PendingStoryFollowUp pending = pendingStoryFollowUps[i];
            if (pending == null || pending.story == null || pending.speaker == null
                || pending.target == null || Time.time > pending.expiresAt)
            {
                if (pending?.arc != null)
                {
                    pending.arc.status = "expired";
                    LLMBrainService.Instance?.UpsertStoryArc(pending.arc);
                    queuedArcIds.Remove(pending.arc.arcId);
                }
                pendingStoryFollowUps.RemoveAt(i);
                continue;
            }
            if (Time.time < pending.executeAt)
                continue;
            if (pending.inFlight)
                continue;
            if (!pending.speaker.CanJoinStoryBeat || !pending.target.CanJoinStoryBeat)
            {
                pending.executeAt = Time.time + 15f;
                continue;
            }

            pending.inFlight = true;
            BeginStoryFollowUp(pending);
        }
    }

    private async void BeginStoryFollowUp(PendingStoryFollowUp pending)
    {
        if (pending == null || pending.story == null)
            return;

        PreparedStoryConversation generated = await GenerateStoryScene(
            pending.speaker, pending.target, pending.story,
            pending.arc?.establishedFact ?? pending.story.topic,
            pending.arc?.stage ?? 1, pending.arc?.establishedFact);
        if (pending.speaker == null || pending.target == null)
            return;
        if (generated == null)
        {
            pending.inFlight = false;
            pending.executeAt = Time.time + UnityEngine.Random.Range(90f, 150f);
            return;
        }

        OfficeActionPoint action = FindEventSpot(pending.target, pending.story);
        if (action == null)
        {
            pending.inFlight = false;
            pending.executeAt = Time.time + 15f;
            return;
        }

        string opening = generated.openingLine;
        pending.generatedMemory = generated?.memory;
        float intentExpiry = Time.time + 50f;
        ConversationIntent intent = new()
        {
            initiatorAgentId = pending.speaker.AgentId,
            initiatorName = pending.speaker.DisplayName,
            intendedPartnerName = pending.target.DisplayName,
            topic = !string.IsNullOrWhiteSpace(generated.topic)
                ? generated.topic : pending.story.topic,
            openingLine = opening,
            generatedByModel = true,
            preparedScript = generated.continuation,
            actionType = action.actionType,
            createdAt = Time.time,
            expiresAt = intentExpiry
        };
        intent.onOpeningSpoken = () => CompleteStoryFollowUp(pending);

        if (!pending.speaker.RequestEventConversation(action, intent))
        {
            pending.inFlight = false;
            pending.executeAt = Time.time + 15f;
            return;
        }

        pending.target.ReceiveSocialInvitation(
            action, pending.speaker.DisplayName, intentExpiry);
        UpdateStoryVisual(pending.story, 1);
        pendingStoryFollowUps.Remove(pending);
    }

    private void CompleteStoryFollowUp(PendingStoryFollowUp pending)
    {
        if (pending?.story == null || pending.speaker == null || pending.target == null)
            return;

        string memory = pending.generatedMemory;
        if (string.IsNullOrWhiteSpace(memory))
            memory = Expand(
                pending.story.followUpMemory, pending.speaker, pending.target);
        if (string.IsNullOrWhiteSpace(memory))
            memory = pending.speaker.DisplayName + " and " + pending.target.DisplayName
                + " followed through on their plan.";

        LLMBrainService brain = LLMBrainService.Instance;
        brain?.Remember(pending.speaker.AgentId, memory);
        brain?.Remember(pending.target.AgentId, memory);
        if (pending.story.kind != OfficeStoryKind.Gossip)
            brain?.RememberWorldEvent(memory);
        if (pending.story.publicAnnouncement)
        {
            string stageLabel = pending.arc != null && pending.arc.stage >= 2
                ? pending.story.title + " Resolved"
                : pending.story.title + " Developed";
            VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
                stageLabel, memory, null, 2.5f);
        }

        if (pending.arc == null)
            return;
        if (pending.arc.stage >= 2)
        {
            UpdateStoryVisual(pending.story, 2);
            LLMBrainService.Instance?.CompleteStoryArc(pending.arc.arcId);
            queuedArcIds.Remove(pending.arc.arcId);
            return;
        }

        pending.arc.stage++;
        pending.arc.establishedFact = memory;
        pending.arc.nextStageWorldTime =
            (LLMBrainService.Instance?.WorldUnixSeconds ?? 0d)
            + (LLMBrainService.Instance?.WorldSecondsFromRealSeconds(90f) ?? 90d);
        LLMBrainService.Instance?.UpsertStoryArc(pending.arc);
        float executeAt = Time.time + 90f;
        pendingStoryFollowUps.Add(new PendingStoryFollowUp
        {
            story = pending.story,
            speaker = pending.speaker,
            target = pending.target,
            arc = pending.arc,
            executeAt = executeAt,
            expiresAt = executeAt + 240f
        });
    }

    private OfficeStoryBeatSO PickStory()
    {
        float total = 0f;
        foreach (OfficeStoryBeatSO story in ambientStories)
            if (story != null && !WasRecentlyUsed(story.storyId))
                total += Mathf.Max(0.01f, story.weight);

        if (total <= 0f)
        {
            recentStoryIds.Clear();
            foreach (OfficeStoryBeatSO story in ambientStories)
                if (story != null)
                    total += Mathf.Max(0.01f, story.weight);
        }
        if (total <= 0f)
            return null;

        float roll = UnityEngine.Random.Range(0f, total);
        OfficeStoryBeatSO fallback = null;
        foreach (OfficeStoryBeatSO story in ambientStories)
        {
            if (story == null || WasRecentlyUsed(story.storyId))
                continue;
            fallback = story;
            roll -= Mathf.Max(0.01f, story.weight);
            if (roll <= 0f)
                return story;
        }
        return fallback;
    }

    private static AIWorkerAgent PickStorySpeaker(List<AIWorkerAgent> candidates, string topic)
    {
        AIWorkerAgent best = candidates[0];
        float bestScore = float.MinValue;
        foreach (AIWorkerAgent candidate in candidates)
        {
            float score = candidate.ScoreConversationTopic(topic)
                + UnityEngine.Random.Range(0f, 4f);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    private async Task<PreparedStoryConversation> GenerateStoryScene(
        AIWorkerAgent speaker, AIWorkerAgent target, OfficeStoryBeatSO story,
        string establishedFact, int stage, string priorOutcome)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || speaker == null || target == null
            || !speaker.TryGetComponent(out AgentConversationController speakerConversation)
            || !target.TryGetComponent(out AgentConversationController targetConversation))
            return null;

        return await brain.GenerateStoryConversationAsync(
            speakerConversation.BuildParticipantContext(),
            targetConversation.BuildParticipantContext(),
            story != null ? story.title : "Office event",
            establishedFact, stage, priorOutcome);
    }

    private void RememberStory(OfficeStoryBeatSO story, AIWorkerAgent speaker,
        AIWorkerAgent target, string memory)
    {
        if (string.IsNullOrWhiteSpace(memory))
            return;

        LLMBrainService brain = LLMBrainService.Instance;
        brain?.Remember(speaker.AgentId, memory);
        brain?.Remember(target.AgentId, memory);
        if (story.kind != OfficeStoryKind.Gossip)
            brain?.RememberWorldEvent(memory);
    }

    private void ScheduleNextAmbientStory(bool storyStarted)
    {
        if (!storyStarted)
        {
            nextAmbientStoryTime = Time.time + UnityEngine.Random.Range(90f, 150f);
            return;
        }

        float minimum = Mathf.Max(20f, minimumAmbientStoryIntervalSeconds);
        float maximum = Mathf.Max(minimum, maximumAmbientStoryIntervalSeconds);
        nextAmbientStoryTime = Time.time + UnityEngine.Random.Range(minimum, maximum);
    }

    private void RememberRecentStory(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId))
            return;
        recentStoryIds.Enqueue(storyId);
        while (recentStoryIds.Count > 3)
            recentStoryIds.Dequeue();
    }

    private bool WasRecentlyUsed(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId))
            return false;
        foreach (string recent in recentStoryIds)
            if (string.Equals(recent, storyId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private AIWorkerAgent FindStoryPartner(AIWorkerAgent reactor,
        IEnumerable<AIWorkerAgent> candidates)
    {
        if (candidates == null)
            return null;
        foreach (AIWorkerAgent candidate in candidates)
            if (candidate != null && candidate != reactor && candidate.CanJoinStoryBeat)
                return candidate;
        return null;
    }

    private static AIWorkerAgent FirstAvailable(IEnumerable<AIWorkerAgent> candidates)
    {
        if (candidates == null)
            return null;
        foreach (AIWorkerAgent candidate in candidates)
            if (candidate != null && candidate.CanJoinStoryBeat)
                return candidate;
        return null;
    }

    private static AIWorkerAgent FirstWorker(IEnumerable<AIWorkerAgent> candidates)
    {
        if (candidates == null)
            return null;
        foreach (AIWorkerAgent candidate in candidates)
            if (candidate != null)
                return candidate;
        return null;
    }

    private static string Expand(string value, AIWorkerAgent speaker, AIWorkerAgent target)
    {
        return (value ?? "")
            .Replace("{speaker}", speaker != null ? speaker.DisplayName : "Someone")
            .Replace("{target}", target != null ? target.DisplayName : "a coworker");
    }

    private static string SafeLower(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    private static string BuildVendingReaction(VendingEventSO evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.characterReaction))
            return evt.characterReaction.Trim();

        string text = (evt.displayName + " " + evt.description).ToLowerInvariant();
        if (text.Contains("coffee") || text.Contains("caffeine") || text.Contains("drink"))
            return "That energy boost arrived at exactly the right time.";
        if (text.Contains("snack") || text.Contains("lunch") || text.Contains("food"))
            return "A real purchase just changed our tiny office.";
        if (evt is IVendingGachaEvent)
            return "The office actually looks different now.";
        return "Something from the vending machine changed our day.";
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
        float expiresAt = Time.time + 60f;
        ConversationIntent intent = new()
        {
            initiatorAgentId = organizer.AgentId,
            initiatorName = organizer.DisplayName,
            intendedPartnerName = birthdayWorker.DisplayName,
            topic = birthdayWorker.DisplayName + "'s birthday",
            openingLine = "",
            generatedByModel = true,
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

    private AIWorkerAgent FindBirthdayWorker(bool requireAvailable = true)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || string.IsNullOrWhiteSpace(worker.Birthday))
                continue;
            if (requireAvailable
                && worker.TryGetComponent(out AgentConversationController conversation)
                && conversation.IsInConversation)
                continue;
            if (IsToday(worker.Birthday))
                return worker;
        }

        return null;
    }

    private static bool IsBirthdayPreparation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.IndexOf("birthday", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return text.IndexOf("gift", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("surprise", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("decorat", StringComparison.OrdinalIgnoreCase) >= 0;
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
        return FindEventSpot(birthdayWorker, null);
    }

    private OfficeActionPoint FindEventSpot(AIWorkerAgent worker, OfficeStoryBeatSO story)
    {
        OfficeActionPoint[] points = FindObjectsOfType<OfficeActionPoint>();
        OfficeActionPoint best = null;
        float bestScore = float.MinValue;
        OfficeActionType preferredType = PreferredStoryLocation(story);

        foreach (OfficeActionPoint point in points)
        {
            if (point == null || !AgentConversationController.IsSocialSpot(point.actionType))
                continue;
            if (AgentConversationController.IsConversationActiveAt(point))
                continue;
            if (point.IsReservedByOther(worker))
                continue;

            float score = point.actionType == OfficeActionType.BreakSpot ? 20f : 10f;
            if (point.actionType == preferredType)
                score += 80f;
            score -= Vector2.Distance(worker.GetPosition(), point.transform.position);
            if (score > bestScore)
            {
                bestScore = score;
                best = point;
            }
        }

        return best;
    }

    private static OfficeActionType PreferredStoryLocation(OfficeStoryBeatSO story)
    {
        string text = ((story?.title ?? "") + " " + (story?.topic ?? ""))
            .ToLowerInvariant();
        if (text.Contains("printer"))
            return OfficeActionType.Printer;
        if (text.Contains("folder") || text.Contains("project")
            || text.Contains("shared drive"))
            return OfficeActionType.Whiteboard;
        if (text.Contains("lunch") || text.Contains("snack"))
            return OfficeActionType.BreakSpot;
        return story != null && story.kind == OfficeStoryKind.Celebration
            ? OfficeActionType.BreakSpot : OfficeActionType.ChatSpot;
    }

    private static void UpdateStoryVisual(OfficeStoryBeatSO story, int stage)
    {
        if (PreferredStoryLocation(story) != OfficeActionType.Printer)
            return;
        OfficePrinterController printer =
            FindFirstObjectByType<OfficePrinterController>();
        printer?.SetStoryStage(stage);
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

        return BirthdayGift.None;
    }

    private void ApplyBirthdayGift(AIWorkerAgent birthdayWorker, BirthdayGift gift)
    {
        if (birthdayWorker == null || gift.kind != BirthdayGiftKind.Hat
            || gift.hat == null || gift.pool == null)
            return;

        if (birthdayWorker.TryGetComponent(out AgentPresentation2D presentation))
        {
            presentation.ApplyHat(
                gift.hat.sprite, gift.pool.standingOffset, gift.pool.sittingOffset,
                gift.hat.localScale);
        }

        VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
            "Birthday Gift", birthdayWorker.DisplayName + " got a new hat.", gift.hat.sprite, 2.5f);
        LLMBrainService.Instance?.RememberWorldEvent(
            birthdayWorker.DisplayName + " received a hat as a birthday gift.");
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
