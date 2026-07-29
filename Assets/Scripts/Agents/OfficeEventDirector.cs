using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class OfficeEventDirector : MonoBehaviour
{
    private sealed class PendingEpisodeExecution
    {
        public OfficeEpisodeBeat beat;
        public bool actionsQueued;
        public bool thoughtHandled;
        public bool groupGathering;
        public AIWorkerAgent thoughtOwner;
        public float notBefore;
        public float expiresAt;
        public readonly List<string> actionSequenceIds = new();
    }

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

    [Header("Remote Episode Director")]
    [SerializeField] private bool enableEpisodeDirector = true;
    [SerializeField, Range(6, 10)] private int episodePackSize = 8;
    [SerializeField, Range(1, 4)] private int episodeRefillThreshold = 3;
    [SerializeField, Min(2f)] private float firstEpisodeDelaySeconds = 10f;
    [SerializeField, Min(20f)] private float minimumEpisodeIntervalSeconds = 30f;
    [SerializeField, Min(20f)] private float maximumEpisodeIntervalSeconds = 50f;
    [SerializeField, Min(20f)] private float episodeExecutionTimeoutSeconds = 60f;
    [SerializeField, Min(5f)] private float failedRefillRetrySeconds = 30f;

    [Header("Ambient Office Stories")]
    [SerializeField] private bool enableAmbientStories = false;
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
    private readonly Queue<OfficeEpisodeBeat> episodeReserve = new();
    private OfficeStoryBeatSO[] ambientStories;
    private float nextCheckTime;
    private float nextAmbientStoryTime;
    private float nextEpisodeTime;
    private float nextEpisodeRefillTime;
    private bool ambientStoryInFlight;
    private bool episodeRefillInFlight;
    private bool episodeReserveRestored;
    private bool clearReserveAfterPendingEpisode;
    private bool loggedMissingEpisodeProvider;
    private int consecutiveEpisodeRefillFailures;
    private PendingEpisodeExecution pendingEpisode;

    public bool ManagesLLMScenes => enableEpisodeDirector;

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
        nextEpisodeTime = Time.time + firstEpisodeDelaySeconds;
        nextEpisodeRefillTime = Time.time + 1f;
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
        if (!enableEpisodeDirector)
            RestorePersistedStoryArcs();
        RestoreEpisodeReserve();
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

        string immediateReaction = enableEpisodeDirector
            ? !string.IsNullOrWhiteSpace(evt.characterReaction)
                ? evt.characterReaction.Trim()
                : evt.description
            : BuildVendingReaction(evt);
        reactor.ReactToWorldEvent(immediateReaction);
        string eventKey = !string.IsNullOrWhiteSpace(evt.eventId)
            ? evt.eventId : evt.displayName;
        if (!string.IsNullOrWhiteSpace(eventKey)
            && nextVendingConversationTimes.TryGetValue(eventKey, out float nextAllowed)
            && Time.unscaledTime < nextAllowed)
            return;
        if (!string.IsNullOrWhiteSpace(eventKey))
            nextVendingConversationTimes[eventKey] =
                Time.unscaledTime + vendingConversationCooldownSeconds;

        if (enableEpisodeDirector)
        {
            string physicalEvent = "A physical vending purchase added "
                + evt.displayName + " to the office: " + evt.description;
            LLMBrainService.Instance?.RememberWorldEvent(physicalEvent);
            PrioritizeFreshWorldContext();
            return;
        }

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
        if (enableEpisodeDirector)
            TickEpisodeDirector();
        else
            TryStartDueFollowUps();

        if (Time.time >= nextCheckTime)
        {
            nextCheckTime = Time.time + checkIntervalSeconds;
            TryStartBirthdayEvent();
        }

        if (!enableEpisodeDirector && enableAmbientStories
            && Time.time >= nextAmbientStoryTime)
        {
            nextAmbientStoryTime = float.MaxValue;
            BeginAmbientStory();
        }
    }

    private void RestoreEpisodeReserve()
    {
        if (episodeReserveRestored)
            return;
        episodeReserveRestored = true;
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return;
        foreach (OfficeEpisodeBeat beat in brain.RestoreEpisodeReserve())
            if (beat != null)
                episodeReserve.Enqueue(beat);
        if (episodeReserve.Count > 0)
            Debug.Log("[Episode director] restored " + episodeReserve.Count
                + " buffered beats.", this);
    }

    private void TickEpisodeDirector()
    {
        workers.RemoveAll(worker => worker == null);
        if (workers.Count < 2)
            return;

        LLMBrainService brain = LLMBrainService.Instance;
        if (!episodeRefillInFlight && Time.time >= nextEpisodeRefillTime
            && episodeReserve.Count < Mathf.Clamp(episodeRefillThreshold, 1, 4))
        {
            if (brain != null && brain.HasRemoteEpisodeProvider)
            {
                loggedMissingEpisodeProvider = false;
                BeginEpisodeRefill();
            }
            else
            {
                if (!loggedMissingEpisodeProvider)
                {
                    loggedMissingEpisodeProvider = true;
                    Debug.LogWarning("[Episode director] no remote provider is ready. "
                        + "Set GROQ_API_KEY or GEMINI_API_KEY and restart Unity. "
                        + "Utility movement remains active.", this);
                }
                nextEpisodeRefillTime = Time.time + 60f;
            }
        }

        while (episodeReserve.Count > 0 && episodeReserve.Peek() == null)
            episodeReserve.Dequeue();
        if (pendingEpisode == null && episodeReserve.Count > 0
            && Time.time >= nextEpisodeTime)
        {
            pendingEpisode = new PendingEpisodeExecution
            {
                beat = episodeReserve.Peek(),
                expiresAt = Time.time + Mathf.Clamp(
                    episodeExecutionTimeoutSeconds, 20f, 60f)
            };
        }

        if (pendingEpisode == null)
            return;
        if (Time.time > pendingEpisode.expiresAt)
        {
            SkipPendingEpisode(pendingEpisode.groupGathering
                ? "group did not gather before timeout"
                : "could not find an executable moment");
            return;
        }
        TryExecutePendingEpisode();
    }

    private async void BeginEpisodeRefill()
    {
        if (episodeRefillInFlight)
            return;
        episodeRefillInFlight = true;
        try
        {
            List<AIWorkerAgent> snapshot =
                workers.FindAll(worker => worker != null);
            int requested = consecutiveEpisodeRefillFailures > 0
                ? 6 : Mathf.Clamp(episodePackSize, 6, 10);
            OfficeEpisodePack pack = LLMBrainService.Instance != null
                ? await LLMBrainService.Instance.GenerateEpisodePackAsync(
                    snapshot, requested)
                : null;
            if (this == null)
                return;

            if (pack?.beats == null || pack.beats.Length == 0)
            {
                consecutiveEpisodeRefillFailures++;
                nextEpisodeRefillTime = Time.time
                    + Mathf.Max(5f, failedRefillRetrySeconds);
                return;
            }

            consecutiveEpisodeRefillFailures = 0;
            foreach (OfficeEpisodeBeat beat in pack.beats)
            {
                if (beat == null || episodeReserve.Count >= 18)
                    continue;
                episodeReserve.Enqueue(beat);
            }
            PersistEpisodeReserve();
            nextEpisodeRefillTime = Time.time + 2f;
        }
        catch (Exception exception)
        {
            consecutiveEpisodeRefillFailures++;
            nextEpisodeRefillTime = Time.time
                + Mathf.Max(5f, failedRefillRetrySeconds);
            Debug.LogWarning("[Episode director] refill failed: "
                + exception.Message, this);
        }
        finally
        {
            if (this != null)
                episodeRefillInFlight = false;
        }
    }

    private void TryExecutePendingEpisode()
    {
        OfficeEpisodeBeat beat = pendingEpisode?.beat;
        if (beat == null)
        {
            SkipPendingEpisode("empty beat");
            return;
        }

        if (!pendingEpisode.thoughtHandled)
        {
            AIWorkerAgent thinker = FindWorker(beat.thoughtAgentId);
            if (thinker != null && !thinker.CanJoinStoryBeat)
                return;
            pendingEpisode.thoughtHandled = true;
            if (thinker != null && !string.IsNullOrWhiteSpace(beat.privateThought))
            {
                pendingEpisode.thoughtOwner = thinker;
                pendingEpisode.notBefore = Time.time + 4f;
                thinker.ShowThought(beat.privateThought);
                LLMBrainService brain = LLMBrainService.Instance;
                if (brain != null)
                    TextUtils.AddRecent(
                        brain.RecentGlobalUtterances, beat.privateThought, 24);
                Debug.Log("[Episode thought] " + thinker.DisplayName + ": "
                    + beat.privateThought, thinker);
                return;
            }
        }
        if (Time.time < pendingEpisode.notBefore)
            return;
        if (pendingEpisode.thoughtOwner != null)
        {
            pendingEpisode.thoughtOwner.HideThought();
            pendingEpisode.thoughtOwner = null;
        }

        switch ((beat.kind ?? "").Trim().ToLowerInvariant())
        {
            case "activity":
                if (!pendingEpisode.actionsQueued)
                {
                    int queued = QueueEpisodeActions(
                        pendingEpisode, true, null, true);
                    pendingEpisode.actionsQueued = true;
                    if (queued == 0)
                    {
                        SkipPendingEpisode("no valid physical action");
                        return;
                    }
                    CompletePendingEpisode();
                }
                return;

            case "phone":
                TryStartEpisodePhoneCall(beat);
                return;

            case "conversation":
                if (!pendingEpisode.actionsQueued)
                {
                    pendingEpisode.actionsQueued = true;
                    if (HasEpisodeActions(beat, "before"))
                    {
                        QueueEpisodeActions(
                            pendingEpisode, true, "before", true);
                        pendingEpisode.notBefore = Time.time + 4f;
                        return;
                    }
                }
                if (Time.time < pendingEpisode.notBefore
                    || HasPendingEpisodeActions(pendingEpisode))
                    return;
                TryStartEpisodeConversation(beat);
                return;

            default:
                SkipPendingEpisode("unknown beat kind");
                return;
        }
    }

    private int QueueEpisodeActions(PendingEpisodeExecution execution,
        bool interruptRoutine, string timingFilter, bool trackSequences)
    {
        if (execution?.beat?.actions == null)
            return 0;

        Dictionary<string, int> stepsByAgent =
            new(StringComparer.OrdinalIgnoreCase);
        int queued = 0;
        foreach (OfficeEpisodeAction action in execution.beat.actions)
        {
            string timing = string.Equals(action?.timing, "after",
                StringComparison.OrdinalIgnoreCase) ? "after" : "before";
            if (!string.IsNullOrWhiteSpace(timingFilter)
                && !string.Equals(timing, timingFilter,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            AIWorkerAgent worker = FindWorker(action?.agentId);
            if (worker == null || action == null
                || !Enum.TryParse(action.actionType, true,
                    out OfficeActionType actionType))
                continue;

            string sequenceId = "episode:" + execution.beat.beatId
                + ":" + worker.AgentId;
            stepsByAgent.TryGetValue(worker.AgentId, out int step);
            step++;
            stepsByAgent[worker.AgentId] = step;
            OfficeDestinationMode mode = ResolveEpisodeDestination(actionType);
            OfficeActivityPlan plan = new()
            {
                actionType = actionType,
                destinationMode = mode,
                destinationHint = action.destinationHint,
                sequenceId = sequenceId,
                sequenceStep = step,
                objective = execution.beat.topic,
                targetAgent = FindWorker(action.targetAgentId)?.DisplayName ?? "",
                durationSeconds = Mathf.Clamp(action.durationSeconds, 2f, 15f),
                reason = action.reason,
                thought = action.thought,
                customActionLabel = actionType == OfficeActionType.Custom
                    ? action.reason : "",
                isDirected = true,
                remainingStartAttempts = 8
            };
            if (!worker.QueueEpisodeActivity(plan, interruptRoutine))
                continue;
            if (trackSequences
                && !execution.actionSequenceIds.Contains(sequenceId))
                execution.actionSequenceIds.Add(sequenceId);
            queued++;
        }
        return queued;
    }

    private static bool HasEpisodeActions(OfficeEpisodeBeat beat,
        string timing)
    {
        if (beat?.actions == null)
            return false;
        foreach (OfficeEpisodeAction action in beat.actions)
        {
            string actionTiming = string.Equals(action?.timing, "after",
                StringComparison.OrdinalIgnoreCase) ? "after" : "before";
            if (string.Equals(actionTiming, timing,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static OfficeDestinationMode ResolveEpisodeDestination(
        OfficeActionType actionType)
    {
        switch (actionType)
        {
            case OfficeActionType.WalkAround:
            case OfficeActionType.Custom:
                return OfficeDestinationMode.FreePosition;
            case OfficeActionType.Think:
            case OfficeActionType.CheckPhone:
                return OfficeDestinationMode.CurrentPosition;
            default:
                return OfficeDestinationMode.ActionPoint;
        }
    }

    private bool HasPendingEpisodeActions(PendingEpisodeExecution execution)
    {
        if (execution == null)
            return false;
        foreach (string sequenceId in execution.actionSequenceIds)
        {
            int separator = sequenceId.LastIndexOf(':');
            string agentId = separator >= 0
                ? sequenceId.Substring(separator + 1) : "";
            AIWorkerAgent worker = FindWorker(agentId);
            if (worker != null && worker.HasEpisodeSequence(sequenceId))
                return true;
        }
        return false;
    }

    private void TryStartEpisodeConversation(OfficeEpisodeBeat beat)
    {
        if (AgentConversationController.IsAnyConversationActive)
            return;
        if (pendingEpisode != null && pendingEpisode.groupGathering)
            return;
        if (beat?.participantIds == null || beat.participantIds.Length < 2
            || beat.participantIds.Length > 4
            || beat.dialogue == null || beat.dialogue.Length < 3)
        {
            SkipPendingEpisode("invalid prepared conversation");
            return;
        }

        List<AIWorkerAgent> participants = new();
        foreach (string participantId in beat.participantIds)
        {
            AIWorkerAgent participant = FindWorker(participantId);
            if (participant == null)
                return;
            if (!participants.Contains(participant))
                participants.Add(participant);
        }
        if (participants.Count != beat.participantIds.Length)
            return;

        AIWorkerAgent initiator = FindWorker(beat.dialogue[0].agentId);
        if (initiator == null || !participants.Contains(initiator))
            return;

        ConversationScript script = BuildPreparedConversation(beat);
        if (script == null || string.IsNullOrWhiteSpace(script.openingLine))
        {
            SkipPendingEpisode("prepared dialogue was unusable");
            return;
        }

        if (participants.Count == 2)
        {
            pendingEpisode.expiresAt = Mathf.Min(
                pendingEpisode.expiresAt, Time.time + 30f);
            AIWorkerAgent target = participants[0] == initiator
                ? participants[1] : participants[0];
            if (!initiator.RequestApproachConversation(
                    target, beat.topic, script.openingLine,
                    highPriority: true, preparedScript: script))
                return;
            QueueEpisodeActions(pendingEpisode, false, "after", false);
        }
        else
        {
            OfficeActionPoint action = FindSharedSocialPoint(participants)
                ?? FindGroupEventSpot(initiator, participants);
            if (action == null)
            {
                pendingEpisode.expiresAt = Mathf.Min(
                    pendingEpisode.expiresAt, Time.time + 20f);
                return;
            }
            float expiresAt = Mathf.Min(
                pendingEpisode.expiresAt, Time.time + 40f);
            PendingEpisodeExecution execution = pendingEpisode;
            execution.expiresAt = expiresAt;
            ConversationIntent intent = new()
            {
                initiatorAgentId = initiator.AgentId,
                initiatorName = initiator.DisplayName,
                intendedPartnerName = "",
                requiredParticipantNames = participants.ConvertAll(
                    participant => participant.DisplayName).ToArray(),
                topic = beat.topic,
                openingLine = script.openingLine,
                generatedByModel = true,
                preparedScript = script,
                actionType = action.actionType,
                createdAt = Time.time,
                expiresAt = expiresAt
            };
            intent.onOpeningSpoken = () =>
            {
                if (pendingEpisode != execution)
                    return;
                QueueEpisodeActions(execution, false, "after", false);
                CompletePendingEpisode();
            };
            execution.groupGathering = true;
            if (!initiator.RequestEventConversation(action, intent))
            {
                execution.groupGathering = false;
                return;
            }
            if (!AgentConversationController.IsAnyConversationActive)
                foreach (AIWorkerAgent participant in participants)
                    if (participant != initiator)
                        participant.ReceiveSocialInvitation(
                            action, initiator.DisplayName, expiresAt,
                            highPriority: true);
            Debug.Log("[Episode gathering] " + beat.topic + ": "
                + string.Join(", ", participants.ConvertAll(
                    participant => participant.DisplayName))
                + " at " + action.actionType, this);
            return;
        }

        CompletePendingEpisode();
    }

    private static OfficeActionPoint FindSharedSocialPoint(
        List<AIWorkerAgent> participants)
    {
        if (participants == null || participants.Count < 2)
            return null;
        foreach (OfficeActionPoint point in
                 FindObjectsOfType<OfficeActionPoint>())
        {
            if (point == null
                || !AgentConversationController.IsSocialSpot(point.actionType)
                || AgentConversationController.IsConversationActiveAt(point))
                continue;
            bool allPresent = participants.TrueForAll(
                participant => participant != null
                    && participant.IsActingAt(point));
            if (allPresent)
                return point;
        }
        return null;
    }

    private static OfficeActionPoint FindGroupEventSpot(
        AIWorkerAgent initiator, List<AIWorkerAgent> participants)
    {
        if (initiator == null || participants == null)
            return null;
        OfficeActionPoint best = null;
        float bestScore = float.MinValue;
        foreach (OfficeActionPoint point in
                 FindObjectsOfType<OfficeActionPoint>())
        {
            if (point == null
                || !AgentConversationController.IsRoutineSocialSpot(
                    point.actionType)
                || AgentConversationController.IsConversationActiveAt(point))
                continue;
            int selectedAlreadyPresent = participants.FindAll(
                participant => participant != null
                    && participant.IsActingAt(point)).Count;
            if (point.AvailableSlots + selectedAlreadyPresent
                < participants.Count)
                continue;
            float score = selectedAlreadyPresent * 40f
                - Vector2.Distance(
                    initiator.GetPosition(), point.transform.position);
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = point;
        }
        return best;
    }

    private ConversationScript BuildPreparedConversation(OfficeEpisodeBeat beat)
    {
        if (beat?.dialogue == null || beat.dialogue.Length < 3)
            return null;
        AIWorkerAgent openingSpeaker = FindWorker(beat.dialogue[0].agentId);
        if (openingSpeaker == null
            || string.IsNullOrWhiteSpace(beat.dialogue[0].line))
            return null;

        ConversationScript script = new()
        {
            openingLine = RemoveOpeningVocative(
                beat.dialogue[0].line.Trim(), beat.participantIds)
        };
        for (int i = 1; i < beat.dialogue.Length; i++)
        {
            AIWorkerAgent speaker = FindWorker(beat.dialogue[i]?.agentId);
            string line = beat.dialogue[i]?.line;
            if (speaker == null || string.IsNullOrWhiteSpace(line))
                continue;
            script.turns.Add(new ConversationTurn
            {
                speaker = speaker.DisplayName,
                line = line.Trim()
            });
        }
        if (script.turns.Count < 2)
            return null;

        if (beat.socialEvent != null
            && !string.IsNullOrWhiteSpace(beat.socialEvent.type)
            && !string.IsNullOrWhiteSpace(beat.socialEvent.subject)
            && !string.IsNullOrWhiteSpace(beat.socialEvent.sourceAgent))
        {
            AIWorkerAgent source = FindWorker(beat.socialEvent.sourceAgent);
            AIWorkerAgent target = FindWorker(beat.socialEvent.targetAgent);
            script.socialEvents.Add(new SocialMemoryEntry
            {
                type = beat.socialEvent.type,
                sourceAgent = source != null
                    ? source.DisplayName : beat.socialEvent.sourceAgent,
                targetAgent = target != null
                    ? target.DisplayName : beat.socialEvent.targetAgent,
                subject = beat.socialEvent.subject,
                isPrivate = beat.socialEvent.isPrivate,
                status = beat.socialEvent.status
            });
        }
        return script;
    }

    private string RemoveOpeningVocative(string line,
        string[] participantIds)
    {
        if (string.IsNullOrWhiteSpace(line) || participantIds == null)
            return line;
        foreach (string participantId in participantIds)
        {
            AIWorkerAgent participant = FindWorker(participantId);
            string name = participant?.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            string[] prefixes = { name + ",", name + ":", name + "!" };
            foreach (string prefix in prefixes)
            {
                if (!line.StartsWith(prefix,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                string remainder = line.Substring(prefix.Length).TrimStart();
                if (string.IsNullOrWhiteSpace(remainder))
                    return line;
                return char.ToUpperInvariant(remainder[0])
                    + remainder.Substring(1);
            }
        }
        return line;
    }

    private void TryStartEpisodePhoneCall(OfficeEpisodeBeat beat)
    {
        if (AgentConversationController.IsAnyConversationActive)
            return;
        if (pendingEpisode != null)
            pendingEpisode.expiresAt = Mathf.Min(
                pendingEpisode.expiresAt, Time.time + 30f);
        if (beat?.participantIds == null || beat.participantIds.Length != 1
            || beat.dialogue == null || beat.dialogue.Length < 2)
        {
            SkipPendingEpisode("invalid prepared phone call");
            return;
        }
        AIWorkerAgent caller = FindWorker(beat.participantIds[0]);
        if (caller == null || !caller.CanJoinStoryBeat)
            return;

        List<string> lines = new();
        foreach (OfficeEpisodeDialogueLine dialogueLine in beat.dialogue)
            if (dialogueLine != null
                && string.Equals(dialogueLine.agentId, caller.AgentId,
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(dialogueLine.line))
                lines.Add(dialogueLine.line.Trim());
        if (lines.Count < 2)
        {
            SkipPendingEpisode("phone call had too few lines");
            return;
        }
        if (caller.StartPreparedPhoneCall(lines, beat.topic))
            CompletePendingEpisode();
    }

    private void CompletePendingEpisode()
    {
        OfficeEpisodeBeat beat = pendingEpisode?.beat;
        if (beat == null)
            return;

        ClearPendingEpisodeThought();
        RecordEpisodeOutcome(beat);
        RemoveEpisodeFromReserve(beat);
        Debug.Log("[Episode started] " + beat.kind + ": " + beat.topic, this);
        pendingEpisode = null;
        if (clearReserveAfterPendingEpisode)
        {
            clearReserveAfterPendingEpisode = false;
            episodeReserve.Clear();
            nextEpisodeRefillTime = 0f;
        }
        PersistEpisodeReserve();

        float minimum = Mathf.Max(20f, minimumEpisodeIntervalSeconds);
        float maximum = Mathf.Max(minimum, maximumEpisodeIntervalSeconds);
        float requested = beat.delaySeconds > 0f
            ? beat.delaySeconds : UnityEngine.Random.Range(minimum, maximum);
        nextEpisodeTime = Time.time + Mathf.Clamp(requested, minimum, maximum);
    }

    private void SkipPendingEpisode(string reason)
    {
        OfficeEpisodeBeat beat = pendingEpisode?.beat;
        if (beat != null)
        {
            Debug.LogWarning("[Episode skipped] " + beat.beatId + " "
                + beat.kind + "/" + beat.topic + ": " + reason, this);
            LLMBrainService brain = LLMBrainService.Instance;
            if (brain != null)
                TextUtils.AddRecent(
                    brain.RecentGlobalTopics, beat.topic, 12);
            RemoveEpisodeFromReserve(beat);
        }
        ClearPendingEpisodeThought();
        pendingEpisode = null;
        if (clearReserveAfterPendingEpisode)
        {
            clearReserveAfterPendingEpisode = false;
            episodeReserve.Clear();
            nextEpisodeRefillTime = 0f;
        }
        nextEpisodeTime = Time.time + 5f;
        PersistEpisodeReserve();
    }

    private void ClearPendingEpisodeThought()
    {
        if (pendingEpisode?.thoughtOwner == null)
            return;
        pendingEpisode.thoughtOwner.HideThought();
        pendingEpisode.thoughtOwner = null;
    }

    private void RemoveEpisodeFromReserve(OfficeEpisodeBeat beat)
    {
        if (beat == null || episodeReserve.Count == 0)
            return;
        int count = episodeReserve.Count;
        bool removed = false;
        for (int i = 0; i < count; i++)
        {
            OfficeEpisodeBeat candidate = episodeReserve.Dequeue();
            if (!removed && candidate != null && string.Equals(
                    candidate.beatId, beat.beatId,
                    StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }
            episodeReserve.Enqueue(candidate);
        }
    }

    private void RecordEpisodeOutcome(OfficeEpisodeBeat beat)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || beat == null)
            return;
        if (!string.IsNullOrWhiteSpace(beat.memory))
        {
            if (beat.participantIds != null)
                foreach (string agentId in beat.participantIds)
                    brain.Remember(agentId, beat.memory);
            if (beat.socialEvent == null || !beat.socialEvent.isPrivate)
                brain.RememberWorldEvent(beat.memory);
        }
        if (!string.IsNullOrWhiteSpace(beat.resolvesSubject))
            brain.CompleteScheduledEpisodeMemory(beat.resolvesSubject);
    }

    private void PersistEpisodeReserve()
    {
        LLMBrainService.Instance?.SetEpisodeReserve(episodeReserve);
    }

    private void PrioritizeFreshWorldContext()
    {
        if (pendingEpisode == null)
            episodeReserve.Clear();
        else
            clearReserveAfterPendingEpisode = true;
        nextEpisodeRefillTime = 0f;
        nextEpisodeTime = Mathf.Min(nextEpisodeTime, Time.time + 5f);
        PersistEpisodeReserve();
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

        if (enableEpisodeDirector)
        {
            string episodeBirthdayTitle = birthdayWorker.DisplayName + "'s Birthday";
            string episodeBirthdayDescription = "Today is " + birthdayWorker.DisplayName
                + "'s birthday. Their coworkers have noticed.";
            completedBirthdayEvents.Add(eventKey);
            LLMBrainService.Instance?.RememberWorldEvent(episodeBirthdayDescription);
            VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
                episodeBirthdayTitle, episodeBirthdayDescription, null, 3.5f);
            ApplyBirthdayGift(birthdayWorker, TryPrepareBirthdayGift(birthdayWorker));
            PrioritizeFreshWorldContext();
            return;
        }

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
