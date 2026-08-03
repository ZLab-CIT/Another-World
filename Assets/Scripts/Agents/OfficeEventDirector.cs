using System;
using System.Collections.Generic;
using UnityEngine;

public class OfficeEventDirector : MonoBehaviour
{
    private sealed class PendingEpisodeExecution
    {
        public OfficeEpisodeBeat beat;
        public bool actionsQueued;
        public bool thoughtHandled;
        public bool conversationRequested;
        public bool groupGathering;
        public AIWorkerAgent thoughtOwner;
        public float notBefore;
        public float expiresAt;
        public readonly List<string> actionSequenceIds = new();
    }

    public static OfficeEventDirector Instance { get; private set; }

    [SerializeField, Min(10f)] private float firstCheckDelaySeconds = 8f;
    [SerializeField, Min(20f)] private float checkIntervalSeconds = 45f;
    [SerializeField, Range(0f, 1f)] private float giftChance = 0.35f;
    [SerializeField, Range(0f, 1f)] private float hatGiftChance = 0.2f;
    [SerializeField] private HatCatalogSO hatCatalog;

    [Header("Remote Episode Director")]
    [SerializeField, Range(6, 10)] private int episodePackSize = 10;
    [SerializeField, Range(1, 4)] private int episodeRefillThreshold = 4;
    [SerializeField, Min(2f)] private float firstEpisodeDelaySeconds = 6f;
    [SerializeField, Min(20f)] private float minimumEpisodeIntervalSeconds = 24f;
    [SerializeField, Min(20f)] private float maximumEpisodeIntervalSeconds = 38f;
    [SerializeField, Min(20f)] private float episodeExecutionTimeoutSeconds = 75f;
    [SerializeField, Min(5f)] private float failedRefillRetrySeconds = 30f;

    [Header("Random Office Events")]
    [SerializeField] private bool enableRandomOfficeEvents = true;
    [SerializeField, Min(5f)] private float firstRandomEventDelaySeconds = 45f;
    [SerializeField, Min(30f)] private float minimumRandomEventIntervalSeconds = 120f;
    [SerializeField, Min(30f)] private float maximumRandomEventIntervalSeconds = 210f;
    [SerializeField, Min(0f)] private float vendingConversationCooldownSeconds = 300f;

    private readonly List<AIWorkerAgent> workers = new();
    private readonly HashSet<string> completedBirthdayEvents = new();
    private readonly Queue<string> recentStoryIds = new();
    private readonly Dictionary<string, float> nextVendingConversationTimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<OfficeEpisodeBeat> episodeReserve = new();
    private OfficeStoryBeatSO[] ambientStories;
    private float nextCheckTime;
    private float nextRandomEventTime;
    private float nextEpisodeTime;
    private float nextEpisodeRefillTime;
    private bool episodeRefillInFlight;
    private bool episodeReserveRestored;
    private bool clearReserveAfterPendingEpisode;
    private bool loggedMissingEpisodeProvider;
    private bool unresolvedStoryRecoveryChecked;
    private float nextStoryActionRecoveryTime;
    private int consecutiveEpisodeRefillFailures;
    private PendingEpisodeExecution pendingEpisode;

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
        OfficeStoryWorldController.Ensure();
        OfficeInteractionHubClient.Ensure();
        nextCheckTime = Time.time + firstCheckDelaySeconds;
        nextRandomEventTime = Time.time + firstRandomEventDelaySeconds;
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
        RestoreEpisodeReserve();
    }

    public void UnregisterWorker(AIWorkerAgent worker)
    {
        workers.Remove(worker);
    }

    public void NotifyVendingEvent(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        if (evt == null)
            return;

        AIWorkerAgent reactor = FirstWorker(targets);
        if (reactor == null)
            reactor = FirstAvailable(workers);
        if (reactor == null)
            return;

        string immediateReaction = !string.IsNullOrWhiteSpace(evt.characterReaction)
            ? evt.characterReaction.Trim()
            : evt.description;
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

        string physicalEvent = "A physical vending purchase added "
            + evt.displayName + " to the office: " + evt.description;
        LLMBrainService.Instance?.RememberWorldEvent(physicalEvent);
        PrioritizeFreshWorldContext();
    }

    private void Update()
    {
        TickEpisodeDirector();

        if (Time.time >= nextCheckTime)
        {
            nextCheckTime = Time.time + checkIntervalSeconds;
            TryStartBirthdayEvent();
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
            if (beat != null && beat.schemaVersion >= 5)
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
        if (!unresolvedStoryRecoveryChecked && brain != null)
        {
            unresolvedStoryRecoveryChecked = true;
            RecoverUnresolvedStoryActions();
            nextStoryActionRecoveryTime = Time.time + 20f;
        }
        else if (brain != null && Time.time >= nextStoryActionRecoveryTime)
        {
            nextStoryActionRecoveryTime = Time.time + 20f;
            RecoverUnresolvedStoryActions();
        }
        if (enableRandomOfficeEvents && Time.time >= nextRandomEventTime
            && pendingEpisode == null && !episodeRefillInFlight
            && episodeReserve.Count <= Mathf.Clamp(episodeRefillThreshold, 1, 4)
            && !HasUnresolvedMajorStory())
            TryInjectRandomOfficeEvent();

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
                    episodeExecutionTimeoutSeconds, 30f, 90f)
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
                ? 5 : Mathf.Clamp(episodePackSize, 6, 8);
            OfficeEpisodePack pack = LLMBrainService.Instance != null
                ? await LLMBrainService.Instance.GenerateEpisodePackAsync(
                    snapshot, requested,
                    OfficeInteractionHubClient.Instance != null
                    && OfficeInteractionHubClient.Instance.ShouldRequestDecision)
                : null;
            if (this == null)
                return;

            if (pack?.beats == null || pack.beats.Length == 0)
            {
                consecutiveEpisodeRefillFailures++;
                nextEpisodeRefillTime = Time.time
                    + GetRefillRetryDelay();
                return;
            }

            consecutiveEpisodeRefillFailures = 0;
            if (pack.audienceDecision != null)
                OfficeInteractionHubClient.Instance?.PublishDecision(
                    pack.audienceDecision);
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
                + GetRefillRetryDelay();
            Debug.LogWarning("[Episode director] refill failed: "
                + exception.Message, this);
        }
        finally
        {
            if (this != null)
                episodeRefillInFlight = false;
        }
    }

    private float GetRefillRetryDelay()
    {
        float baseDelay = Mathf.Max(10f, failedRefillRetrySeconds);
        int exponent = Mathf.Clamp(consecutiveEpisodeRefillFailures - 1, 0, 4);
        return Mathf.Min(300f, baseDelay * Mathf.Pow(2f, exponent));
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
            AIWorkerAgent target = FindWorker(action.targetAgentId);
            bool deliverItem = target != null && target != worker
                && (actionType == OfficeActionType.VendingMachine
                    || actionType == OfficeActionType.CoffeeMachine);
            OfficeActivityPlan plan = new()
            {
                actionType = actionType,
                destinationMode = mode,
                destinationHint = action.destinationHint,
                sequenceId = sequenceId,
                sequenceStep = step,
                objective = execution.beat.topic,
                targetAgent = target?.DisplayName ?? "",
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
            queued++;
            if (deliverItem)
            {
                step++;
                stepsByAgent[worker.AgentId] = step;
                worker.QueueEpisodeActivity(new OfficeActivityPlan
                {
                    actionType = OfficeActionType.ApproachColleague,
                    destinationMode = OfficeDestinationMode.FollowAgent,
                    sequenceId = sequenceId,
                    sequenceStep = step,
                    objective = execution.beat.topic,
                    targetAgent = target.DisplayName,
                    durationSeconds = 3f,
                    reason = action.reason,
                    giveHeldItemToTarget = true,
                    isDirected = true,
                    remainingStartAttempts = 8
                }, false);
            }
            if (trackSequences
                && !execution.actionSequenceIds.Contains(sequenceId))
                execution.actionSequenceIds.Add(sequenceId);
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
        if (!AgentConversationController.HasConversationCapacity)
            return;
        if (pendingEpisode != null && pendingEpisode.conversationRequested)
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
                pendingEpisode.expiresAt, Time.time + 40f);
            AIWorkerAgent target = participants[0] == initiator
                ? participants[1] : participants[0];
            PendingEpisodeExecution execution = pendingEpisode;
            execution.conversationRequested = true;
            if (!initiator.RequestApproachConversation(
                    target, beat.topic, script.openingLine,
                    highPriority: true, preparedScript: script,
                    onConversationStarted: () =>
                    {
                        if (pendingEpisode != execution)
                            return;
                        QueueEpisodeActions(execution, false, "after", false);
                        CompletePendingEpisode();
                    }))
            {
                execution.conversationRequested = false;
                return;
            }
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
            execution.conversationRequested = true;
            ConversationIntent intent = new()
            {
                initiatorAgentId = initiator.AgentId,
                initiatorName = initiator.DisplayName,
                intendedPartnerName = "",
                requiredParticipantNames = participants.ConvertAll(
                    participant => participant.DisplayName).ToArray(),
                topic = beat.topic,
                openingLine = script.openingLine,
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
                execution.conversationRequested = false;
                execution.groupGathering = false;
                return;
            }
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
        TextUtils.AddRecent(brain.RecentGlobalTopics, beat.topic, 24);
        if (beat.dialogue != null)
            foreach (OfficeEpisodeDialogueLine line in beat.dialogue)
                if (line != null)
                    TextUtils.AddRecent(
                        brain.RecentGlobalUtterances, line.line, 48);
        if (!string.IsNullOrWhiteSpace(beat.memory))
        {
            if (beat.participantIds != null)
                foreach (string agentId in beat.participantIds)
                    brain.Remember(agentId, beat.memory);
        }
        if (!string.IsNullOrWhiteSpace(beat.resolvesSubject))
            brain.CompleteScheduledEpisodeMemory(beat.resolvesSubject);
        OfficeInteractionHubClient.Instance?.RecordWorldEvent(
            "episode", beat.topic,
            string.IsNullOrWhiteSpace(beat.memory) ? beat.topic : beat.memory,
            beat.participantIds ?? Array.Empty<string>());
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

    private AIWorkerAgent FindWorker(string agentId)
    {
        foreach (AIWorkerAgent worker in workers)
            if (worker != null && string.Equals(worker.AgentId, agentId,
                    StringComparison.OrdinalIgnoreCase))
                return worker;
        return null;
    }

    private void TryInjectRandomOfficeEvent()
    {
        if (ambientStories == null || ambientStories.Length == 0)
        {
            ScheduleNextRandomEvent(false);
            return;
        }

        List<AIWorkerAgent> available =
            workers.FindAll(worker => worker != null && worker.CanJoinStoryBeat);
        if (available.Count < 2)
        {
            ScheduleNextRandomEvent(false);
            return;
        }

        OfficeStoryBeatSO story = PickStory();
        if (story == null)
        {
            ScheduleNextRandomEvent(false);
            return;
        }

        AIWorkerAgent preferredActor = FindWorker(story.preferredActorAgentId);
        AIWorkerAgent speaker = preferredActor != null
            && available.Contains(preferredActor)
            ? preferredActor
            : PickStorySpeaker(available, story.topic);
        available.Remove(speaker);
        List<AIWorkerAgent> participants = new() { speaker };
        int participantCount = Mathf.Clamp(
            story.participantCount, 2, Mathf.Min(4, available.Count + 1));
        while (participants.Count < participantCount && available.Count > 0)
        {
            int index = UnityEngine.Random.Range(0, available.Count);
            participants.Add(available[index]);
            available.RemoveAt(index);
        }
        if (story.requiredAction == OfficeActionType.Celebrate)
        {
            participants.Clear();
            participants.Add(speaker);
            foreach (AIWorkerAgent worker in workers)
                if (worker != null && !participants.Contains(worker))
                    participants.Add(worker);
        }
        AIWorkerAgent target = participants.Count > 1
            ? participants[1] : speaker;
        string memory = Expand(story.memory, speaker, target);
        if (string.IsNullOrWhiteSpace(memory))
            memory = Expand(story.topic, speaker, target);
        string worldEvent = story.title + ": " + memory;

        LLMBrainService brain = LLMBrainService.Instance;
        foreach (AIWorkerAgent participant in participants)
            brain?.Remember(participant.AgentId, memory);
        if (story.officeWideKnowledge)
            brain?.RememberWorldEvent(worldEvent);
        if (story.requiresAction)
            brain?.RecordOfficeStoryStarted(story.storyId, speaker.AgentId,
                participants.ConvertAll(participant => participant.AgentId));
        UpdateStoryVisual(story, 1);
        QueueRequiredStoryActions(story, speaker, participants);
        if (story.publicAnnouncement)
            VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
                story.title, memory, null, 3.5f);

        RememberRecentStory(story.storyId);
        OfficeInteractionHubClient.Instance?.RecordWorldEvent(
            "office_story", story.title, memory,
            participants.ConvertAll(participant => participant.AgentId).ToArray());
        ScheduleNextRandomEvent(true);
        if (story.officeWideKnowledge)
            PrioritizeFreshWorldContext();
        Debug.Log("[Office event] " + worldEvent, this);
    }

    private void QueueRequiredStoryActions(OfficeStoryBeatSO story,
        AIWorkerAgent fallbackActor, IEnumerable<AIWorkerAgent> participants)
    {
        if (story == null || !story.requiresAction
            || story.requiredAction == OfficeActionType.Custom)
            return;

        AIWorkerAgent actor = FindWorker(story.preferredActorAgentId)
            ?? fallbackActor;
        if (actor == null)
            return;

        List<AIWorkerAgent> ordered = new() { actor };
        if (participants != null)
            foreach (AIWorkerAgent participant in participants)
                if (participant != null && !ordered.Contains(participant))
                    ordered.Add(participant);

        string sequenceBase = "story:" + story.storyId + ":"
            + Time.frameCount;
        int queued = 0;
        PersistedOfficeStoryState storyState =
            LLMBrainService.Instance?.GetOfficeStoryState(story.storyId);
        foreach (AIWorkerAgent participant in ordered)
        {
            if (story.requiredAction == OfficeActionType.Celebrate
                && HasVisited(storyState, participant.AgentId))
                continue;
            OfficeActivityPlan plan = new()
            {
                actionType = story.requiredAction,
                destinationMode = ResolveEpisodeDestination(story.requiredAction),
                sequenceId = sequenceBase + ":" + participant.AgentId,
                sequenceStep = 1,
                objective = story.topic,
                durationSeconds = Mathf.Clamp(story.actionDurationSeconds
                    + (participant == actor ? 0f : 2f), 2f, 15f),
                reason = participant == actor
                    ? story.topic
                    : "join coworkers responding to " + story.title,
                isDirected = true,
                remainingStartAttempts = 12
            };
            if (participant.QueueEpisodeActivity(plan, true))
                queued++;
        }
        if (queued > 0)
            Debug.Log("[Office event action] " + queued + " workers -> "
                + story.requiredAction, this);
    }

    private void RecoverUnresolvedStoryActions()
    {
        if (ambientStories == null)
            return;
        foreach (OfficeStoryBeatSO story in ambientStories)
        {
            if (story == null || !story.requiresAction
                || !HasUnresolvedStoryOccurrence(story)
                || HasQueuedStoryAction(story.storyId))
                continue;
            LLMBrainService brain = LLMBrainService.Instance;
            if (brain != null && !brain.HasOfficeStoryState(story.storyId))
                brain.RecordOfficeStoryStarted(story.storyId);
            PersistedOfficeStoryState state =
                brain?.GetOfficeStoryState(story.storyId);
            if (state != null && state.visualStage == 0)
                brain.SetOfficeStoryVisualStage(story.storyId, 1);
            UpdateStoryVisual(story,
                state != null ? Mathf.Max(1, state.visualStage) : 1);
            AIWorkerAgent actor = FindWorker(state?.actorAgentId)
                ?? FirstAvailable(workers);
            List<AIWorkerAgent> participants = new();
            if (state?.participantAgentIds != null)
                foreach (string participantId in state.participantAgentIds)
                {
                    AIWorkerAgent participant = FindWorker(participantId);
                    if (participant != null)
                        participants.Add(participant);
                }
            QueueRequiredStoryActions(story, actor, participants);
        }
    }

    private bool HasQueuedStoryAction(string storyId)
    {
        string prefix = "story:" + storyId + ":";
        foreach (AIWorkerAgent worker in workers)
            if (worker != null && worker.HasSequenceWithPrefix(prefix))
                return true;
        return false;
    }

    public void NotifyStoryActionCompleted(
        OfficeActionType actionType, AIWorkerAgent actor)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || ambientStories == null)
            return;
        foreach (OfficeStoryBeatSO story in ambientStories)
        {
            if (story == null || !story.requiresAction
                || story.requiredAction != actionType
                || !HasUnresolvedStoryOccurrence(story))
                continue;
            PersistedOfficeStoryState state =
                brain.GetOfficeStoryState(story.storyId);
            if (story.requiredAction == OfficeActionType.Celebrate)
            {
                brain.RecordOfficeStoryVisit(story.storyId, actor?.AgentId);
                state = brain.GetOfficeStoryState(story.storyId);
                if (!AllWorkersVisited(state))
                    return;
            }
            else if (state != null
                && !string.IsNullOrWhiteSpace(state.actorAgentId)
                && actor != null && !string.Equals(state.actorAgentId,
                    actor.AgentId, StringComparison.OrdinalIgnoreCase))
                continue;
            brain.RecordOfficeStoryResolved(story.storyId);
            string resolution = !string.IsNullOrWhiteSpace(
                    story.resolutionMemory)
                ? Expand(story.resolutionMemory, actor, actor)
                : story.title + " was resolved by "
                    + (actor != null ? actor.DisplayName : "an agent") + ".";
            if (story.officeWideKnowledge)
                brain.RememberWorldEvent(StoryResolutionMarker(story) + " "
                    + resolution);
            if (actor != null)
                brain.Remember(actor.AgentId, resolution);
            OfficeInteractionHubClient.Instance?.RecordWorldEvent(
                "office_story_resolved", story.title, resolution,
                actor != null ? actor.AgentId : "");
            UpdateStoryVisual(story, 3);
            Debug.Log("[Office event resolved] " + story.title
                + " by " + (actor != null ? actor.DisplayName : "an agent"), this);
            return;
        }
    }

    private bool AllWorkersVisited(PersistedOfficeStoryState state)
    {
        if (state?.visitorAgentIds == null)
            return false;
        foreach (AIWorkerAgent worker in workers)
            if (worker != null && !HasVisited(state, worker.AgentId))
                return false;
        return workers.Count > 0;
    }

    private static bool HasVisited(PersistedOfficeStoryState state,
        string agentId)
    {
        if (state?.visitorAgentIds == null
            || string.IsNullOrWhiteSpace(agentId))
            return false;
        foreach (string visitor in state.visitorAgentIds)
            if (string.Equals(visitor, agentId,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool HasUnresolvedStoryOccurrence(OfficeStoryBeatSO story)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || story == null)
            return false;
        if (brain.HasOfficeStoryState(story.storyId))
            return brain.HasPendingOfficeStory(story.storyId);
        int started = 0;
        int resolved = 0;
        string marker = StoryResolutionMarker(story);
        foreach (string worldEvent in brain.WorldEvents)
        {
            if (string.IsNullOrWhiteSpace(worldEvent))
                continue;
            if (worldEvent.StartsWith(story.title + ":",
                    StringComparison.OrdinalIgnoreCase))
                started++;
            if (worldEvent.StartsWith(marker,
                    StringComparison.OrdinalIgnoreCase))
                resolved++;
        }
        if (HasStoryMemoryEvidence(brain, story))
            started = Mathf.Max(started, 1);
        return started > resolved;
    }

    private static bool HasStoryMemoryEvidence(
        LLMBrainService brain, OfficeStoryBeatSO story)
    {
        string keyword = GetStoryMemoryKeyword(story?.title);
        if (string.IsNullOrWhiteSpace(keyword))
            return false;
        foreach (AIWorkerAgent worker in Instance.workers)
        {
            AgentProfile profile = brain.GetProfile(worker?.AgentId);
            if (profile == null)
                continue;
            foreach (string memory in profile.memory)
                if (TextUtils.ContainsIgnoreCase(memory, keyword))
                    return true;
            foreach (SocialMemoryEntry social in profile.socialMemory)
                if (social != null
                    && TextUtils.ContainsIgnoreCase(social.subject, keyword))
                    return true;
        }
        return false;
    }

    private static string GetStoryMemoryKeyword(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        string[] ignored = { "office", "story", "mystery", "event", "the" };
        string[] words = title.Split(
            new[] { ' ', '-', '_', ':', '.', ',' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words)
        {
            string clean = word.Trim().ToLowerInvariant();
            if (clean.Length >= 5 && Array.IndexOf(ignored, clean) < 0)
                return clean;
        }
        return "";
    }

    private static string StoryResolutionMarker(OfficeStoryBeatSO story)
    {
        return "[Story resolved] " + story.storyId;
    }

    private OfficeStoryBeatSO PickStory()
    {
        List<OfficeStoryBeatSO> candidates = new();
        foreach (OfficeStoryBeatSO story in ambientStories)
            if (IsVisualStory(story) && !WasStartedToday(story)
                && !WasRecentlyUsed(story, true))
                candidates.Add(story);
        if (candidates.Count == 0)
            foreach (OfficeStoryBeatSO story in ambientStories)
                if (IsVisualStory(story) && !WasStartedToday(story)
                    && !WasRecentlyUsed(story, false))
                    candidates.Add(story);
        if (candidates.Count == 0)
            foreach (OfficeStoryBeatSO story in ambientStories)
                if (IsVisualStory(story) && !WasStartedToday(story))
                    candidates.Add(story);

        float total = 0f;
        foreach (OfficeStoryBeatSO story in candidates)
            total += Mathf.Max(0.01f, story.weight);
        if (total <= 0f)
            return null;

        float roll = UnityEngine.Random.Range(0f, total);
        foreach (OfficeStoryBeatSO story in candidates)
        {
            roll -= Mathf.Max(0.01f, story.weight);
            if (roll <= 0f)
                return story;
        }
        return candidates[candidates.Count - 1];
    }

    private static bool WasStartedToday(OfficeStoryBeatSO story)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        PersistedOfficeStoryState state =
            brain?.GetOfficeStoryState(story?.storyId);
        return state != null && !string.IsNullOrWhiteSpace(
                state.startedCalendarDate)
            && string.Equals(state.startedCalendarDate,
                brain.WorldDateTime.ToString("yyyy-MM-dd"),
                StringComparison.Ordinal);
    }

    private static bool IsVisualStory(OfficeStoryBeatSO story)
    {
        return story != null && story.requiresAction
            && (IsPrinterStory(story)
                || (OfficeStoryWorldController.Instance != null
                    && OfficeStoryWorldController.Instance.SupportsStory(
                        story.storyId)));
    }

    private bool HasUnresolvedMajorStory()
    {
        if (ambientStories == null)
            return false;
        foreach (OfficeStoryBeatSO story in ambientStories)
            if (IsVisualStory(story) && HasUnresolvedStoryOccurrence(story))
                return true;
        return false;
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

    private void ScheduleNextRandomEvent(bool started)
    {
        if (!started)
        {
            nextRandomEventTime = Time.time + UnityEngine.Random.Range(30f, 60f);
            return;
        }

        float minimum = Mathf.Max(30f, minimumRandomEventIntervalSeconds);
        float maximum = Mathf.Max(minimum, maximumRandomEventIntervalSeconds);
        nextRandomEventTime = Time.time + UnityEngine.Random.Range(minimum, maximum);
    }

    private void RememberRecentStory(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId))
            return;
        recentStoryIds.Enqueue(storyId);
        while (recentStoryIds.Count > 3)
            recentStoryIds.Dequeue();
    }

    private bool WasRecentlyUsed(OfficeStoryBeatSO story, bool includeWorldHistory)
    {
        if (story == null || string.IsNullOrWhiteSpace(story.storyId))
            return false;
        foreach (string recent in recentStoryIds)
            if (string.Equals(recent, story.storyId,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        if (!includeWorldHistory || string.IsNullOrWhiteSpace(story.title))
            return false;
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return false;
        PersistedOfficeStoryState state =
            brain.GetOfficeStoryState(story.storyId);
        if (includeWorldHistory && state != null && state.started > 0)
            return true;
        foreach (string worldEvent in brain.WorldEvents)
            if (!string.IsNullOrWhiteSpace(worldEvent)
                && worldEvent.StartsWith(story.title + ":",
                    StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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

    private void TryStartBirthdayEvent()
    {
        AIWorkerAgent birthdayWorker = FindBirthdayWorker();
        if (birthdayWorker == null)
            return;

        string eventKey = DateTime.Today.ToString("yyyy-MM-dd") + ":" + birthdayWorker.AgentId;
        if (completedBirthdayEvents.Contains(eventKey))
            return;

        string birthdayTitle = birthdayWorker.DisplayName + "'s Birthday";
        string birthdayDescription = "Today is " + birthdayWorker.DisplayName
            + "'s birthday. Their coworkers have noticed.";
        completedBirthdayEvents.Add(eventKey);
        LLMBrainService.Instance?.RememberWorldEvent(birthdayDescription);
        VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
            birthdayTitle, birthdayDescription, null, 3.5f);
        OfficeInteractionHubClient.Instance?.RecordWorldEvent(
            "birthday", birthdayTitle, birthdayDescription,
            birthdayWorker.AgentId);
        ApplyBirthdayGift(birthdayWorker, TryPrepareBirthdayGift(birthdayWorker));
        PrioritizeFreshWorldContext();
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

    private static bool IsPrinterStory(OfficeStoryBeatSO story)
    {
        string text = ((story?.title ?? "") + " " + (story?.topic ?? ""))
            .ToLowerInvariant();
        return text.Contains("printer");
    }

    private static void UpdateStoryVisual(OfficeStoryBeatSO story, int stage)
    {
        if (story == null)
            return;
        OfficeStoryWorldController.Instance?.ShowStage(story.storyId, stage);
        if (IsPrinterStory(story))
        {
            OfficePrinterController printer =
                FindFirstObjectByType<OfficePrinterController>();
            printer?.SetStoryStage(stage >= 3 ? 2 : 0);
        }
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
        OfficeInteractionHubClient.Instance?.RecordWorldEvent(
            "birthday_gift", "Birthday Gift",
            birthdayWorker.DisplayName + " received a birthday gift.",
            birthdayWorker.AgentId);
    }

    private enum BirthdayGiftKind
    {
        None,
        Hat
    }

    private struct BirthdayGift
    {
        public BirthdayGiftKind kind;
        public HatCatalogSO.HatEntry hat;
        public HatCatalogSO.HatPool pool;

        public static BirthdayGift None => new() { kind = BirthdayGiftKind.None };

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
