using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

[RequireComponent(typeof(AIWorkerAgent))]
public class AgentConversationController : MonoBehaviour
{
    private static readonly HashSet<OfficeActionPoint> activeConversationActions = new();
    private static AgentConversationController activeConversationOwner;

    [SerializeField, Min(0.1f)] private float participantRadius = 1.5f;
    [SerializeField, Min(0.5f)] private float conversationSeparationRadius = 2.25f;
    [SerializeField, Min(1)] private int maxTurns = 5;
    [SerializeField, Min(0.5f)] private float minimumLineSeconds = 3.4f;
    [SerializeField, Min(0.5f)] private float maximumLineSeconds = 6.2f;
    [SerializeField, Min(0f)] private float betweenTurnsSeconds = 1.6f;

    private readonly Dictionary<string, int> affinity = new();
    private AIWorkerAgent owner;
    private OfficeActionPoint ownedConversationAction;
    private float nextSocialCheckTime;
    private bool inConversation;
    public bool IsInConversation => inConversation;
    public bool IsSociallyCoolingDown => Time.time < nextSocialCheckTime;

    public static bool IsConversationActiveAt(OfficeActionPoint action)
    {
        return action != null && activeConversationActions.Contains(action);
    }

    private void Awake()
    {
        owner = GetComponent<AIWorkerAgent>();
    }

    private void OnDisable()
    {
        if (activeConversationOwner == this)
            activeConversationOwner = null;
        if (ownedConversationAction != null)
            activeConversationActions.Remove(ownedConversationAction);
        ownedConversationAction = null;
        inConversation = false;
        owner?.HideThought();
        owner?.HideSpeech();
    }

    public void OnStartedActing(OfficeActionPoint action)
    {
        nextSocialCheckTime = Time.time + 0.5f;
        TryStart(action);
    }

    public void Tick(OfficeActionPoint action)
    {
        if (action == null || !IsSocialSpot(action.actionType) || Time.time < nextSocialCheckTime)
            return;

        nextSocialCheckTime = Time.time + 0.7f;
        TryStart(action);
    }

    public string BuildRelationships()
    {
        string result = owner != null ? owner.GetEstablishedRelationships() : "";
        LLMBrainService brain = LLMBrainService.Instance;
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (owner != null && brain != null && crowd != null)
        {
            int added = 0;
            foreach (AIWorkerAgent worker in crowd.Workers)
            {
                if (worker == null || worker == owner || added >= 4)
                    continue;
                string dynamicRelationship =
                    brain.BuildRelationshipContext(owner.AgentId, worker.AgentId);
                result += (result.Length > 0 ? "; " : "") + worker.DisplayName + ": "
                    + dynamicRelationship;
                added++;
            }
        }

        int count = 0;
        foreach (KeyValuePair<string, int> relationship in affinity)
        {
            if (relationship.Value <= 0 || count >= 4)
                continue;

            string familiarity = relationship.Value >= 6
                ? "very familiar from frequent conversations"
                : relationship.Value >= 3
                    ? "comfortable after several conversations"
                    : "someone they spoke with recently";
            result += (result.Length > 0 ? "; " : "") + relationship.Key + " is " + familiarity;
            count++;
        }

        return result;
    }

    public ConversationParticipantContext BuildParticipantContext()
    {
        return new ConversationParticipantContext
        {
            agentId = owner.AgentId,
            displayName = owner.DisplayName,
            relationships = BuildRelationships(),
            currentState = owner.GetLLMStateSummary()
        };
    }

    public List<AIWorkerAgent> FindPotentialPartners(OfficeActionPoint action)
    {
        List<AIWorkerAgent> result = new();
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null)
            return result;

        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker == null || worker == owner)
                continue;
            AgentConversationController controller = GetController(worker);
            if (controller != null && controller.inConversation)
                continue;
            result.Add(worker);
        }

        result.Sort((left, right) => PartnerScore(right, action).CompareTo(PartnerScore(left, action)));
        return result;
    }

    public ConversationIntent CreateLocalConversationIntent(OfficeActionPoint action, float expiresAt)
    {
        List<AIWorkerAgent> candidates = FindPotentialPartners(action);
        if (action == null || candidates.Count == 0)
            return null;

        List<AIWorkerAgent> alreadyThere = candidates.FindAll(candidate => candidate.IsActingAt(action));
        if (alreadyThere.Count >= 2 && UnityEngine.Random.value < 0.35f)
        {
            return new ConversationIntent
            {
                initiatorAgentId = owner.AgentId,
                initiatorName = owner.DisplayName,
                intendedPartnerName = "",
                topic = "",
                openingLine = "",
                actionType = action.actionType,
                createdAt = Time.time,
                expiresAt = expiresAt
            };
        }

        AIWorkerAgent partner = alreadyThere.Count > 0
            ? alreadyThere[UnityEngine.Random.Range(0, alreadyThere.Count)]
            : affinity.Count > 0 && UnityEngine.Random.value < 0.55f
                ? candidates[0]
                : candidates[UnityEngine.Random.Range(0, candidates.Count)];
        return new ConversationIntent
        {
            initiatorAgentId = owner.AgentId,
            initiatorName = owner.DisplayName,
            intendedPartnerName = partner.DisplayName,
            topic = "",
            openingLine = "",
            actionType = action.actionType,
            createdAt = Time.time,
            expiresAt = expiresAt
        };
    }

    private float PartnerScore(AIWorkerAgent candidate, OfficeActionPoint action)
    {
        if (candidate == null)
            return float.MinValue;
        float score = affinity.TryGetValue(candidate.DisplayName, out int closeness) ? closeness * 4f : 0f;
        score += (LLMBrainService.Instance?.GetRelationshipScore(
            owner.AgentId, candidate.AgentId) ?? 0f) * 3f;
        if (action != null && candidate.IsActingAt(action))
            score += 100f;
        return score;
    }

    private void SetCooldown(float seconds)
    {
        nextSocialCheckTime = Mathf.Max(nextSocialCheckTime, Time.time + seconds);
    }

    private void TryStart(OfficeActionPoint action)
    {
        if (inConversation || action == null || !IsSocialSpot(action.actionType))
            return;
        if (activeConversationActions.Contains(action))
            return;

        List<AIWorkerAgent> participants = FindNearbyParticipants(action, participantRadius);
        if (participants.Count == 0)
            return;

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return;

        List<AIWorkerAgent> speakers = new() { owner };
        speakers.AddRange(participants);
        AIWorkerAgent starter = SelectConversationStarter(action, speakers, out ConversationIntent intent);
        if (starter != owner)
            return;
        bool featured = intent?.preparedScript != null || intent?.onOpeningSpoken != null;
        if (!featured && !(intent?.routineConversationReserved ?? false)
            && !brain.TryReserveRoutineConversation())
        {
            EndSilentSocialWait(speakers);
            return;
        }

        BeginGeneratedConversation(action, participants, intent);
    }

    private static void EndSilentSocialWait(List<AIWorkerAgent> speakers)
    {
        if (speakers == null)
            return;
        foreach (AIWorkerAgent speaker in speakers)
            speaker?.EndSilentSocialWait();
    }

    private AIWorkerAgent SelectConversationStarter(OfficeActionPoint action,
        List<AIWorkerAgent> speakers, out ConversationIntent selectedIntent)
    {
        selectedIntent = null;
        AIWorkerAgent selectedAgent = null;
        int selectedPriority = -1;

        foreach (AIWorkerAgent speaker in speakers)
        {
            if (speaker == null)
                continue;

            string expectedPartner = speaker.GetExpectedSocialPartner(action);
            if (!string.IsNullOrWhiteSpace(expectedPartner)
                && !ContainsSpeaker(speakers, expectedPartner))
                continue;

            ConversationIntent intent = speaker.GetConversationIntent(action);
            if (intent == null)
                continue;

            bool hasTarget = !string.IsNullOrWhiteSpace(intent.intendedPartnerName);
            bool targetPresent = !hasTarget || ContainsSpeaker(speakers, intent.intendedPartnerName);
            if (!targetPresent)
                continue;

            int priority = targetPresent && hasTarget ? 3 : targetPresent ? 2 : 1;
            if (priority < selectedPriority
                || (priority == selectedPriority && selectedIntent != null
                    && intent.createdAt >= selectedIntent.createdAt))
                continue;

            selectedPriority = priority;
            selectedAgent = speaker;
            selectedIntent = intent;
        }

        if (selectedAgent != null)
            return selectedAgent;
        return null;
    }

    private bool BeginConversation(OfficeActionPoint action, List<AIWorkerAgent> participants,
        string line, string topic, string intendedPartnerName, Action onOpeningSpoken = null,
        ConversationScript preparedScript = null)
    {
        if (activeConversationOwner != null && activeConversationOwner != this)
            return false;
        if (action != null && activeConversationActions.Contains(action))
            return false;
        participants = SelectConversationPair(participants, intendedPartnerName);
        if (participants.Count == 0 || HasUnrelatedConversationNearby(participants))
            return false;
        if (action != null)
        {
            activeConversationActions.Add(action);
            ownedConversationAction = action;
        }
        activeConversationOwner = this;

        owner.ClearConversationDirective();
        SetConversationState(owner, true);
        foreach (AIWorkerAgent participant in participants)
        {
            if (participant == null)
                continue;

            participant.ClearConversationDirective();
            SetConversationState(participant, true);
        }

        // The complete script is generated once while the opening is visible. Keep
        // every captured participant at the point through the worst-case model wait.
        ExtendAll(participants, 35f);
        owner.ApplyEffects(0f, 0f, 6f, 0f);

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
        {
            EndConversation(participants);
            if (action != null)
                activeConversationActions.Remove(action);
            ownedConversationAction = null;
            if (activeConversationOwner == this)
                activeConversationOwner = null;
            return false;
        }

        RunConversation(action, participants, line, topic, intendedPartnerName,
            onOpeningSpoken, preparedScript);
        return true;
    }

    private void BeginGeneratedConversation(OfficeActionPoint action,
        List<AIWorkerAgent> participants, ConversationIntent intent)
    {
        if (intent == null)
            return;

        string intendedPartner = intent.intendedPartnerName ?? "";
        string opener = intent.openingLine?.Trim() ?? "";
        string topic = intent.topic?.Trim() ?? "";

        BeginConversation(action, participants, opener, topic, intendedPartner,
            intent.onOpeningSpoken, intent.preparedScript);
    }

    public bool BeginDirectConversation(AIWorkerAgent target, string openingLine, string topic,
        ConversationScript preparedScript = null, bool routineConversationReserved = false)
    {
        AgentConversationController targetConversation = GetController(target);
        if (target == null || target == owner || inConversation
            || targetConversation == null || targetConversation.inConversation
            || HasUnrelatedConversationNearby(new List<AIWorkerAgent> { target }))
            return false;
        if (preparedScript == null && !routineConversationReserved
            && !(LLMBrainService.Instance?.TryReserveRoutineConversation() ?? false))
            return false;

        return BeginConversation(null, new List<AIWorkerAgent> { target }, openingLine,
            string.IsNullOrWhiteSpace(topic) ? openingLine : topic,
            target.DisplayName, preparedScript: preparedScript);
    }

    private async void RunConversation(OfficeActionPoint action, List<AIWorkerAgent> participants,
        string openerLine, string topic, string intendedPartnerName, Action onOpeningSpoken,
        ConversationScript preparedScript)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        List<AIWorkerAgent> speakers = new() { owner };
        foreach (AIWorkerAgent participant in participants)
            if (participant != null && participant != owner)
                speakers.Add(participant);

        HideAll(speakers);
        List<ConversationTurn> completedTurns = new();
        List<SocialMemoryEntry> completedSocialEvents = null;
        List<ConversationParticipantContext> contexts = null;
        bool conversationSpoken = false;
        string spokenOpening = "";

        try
        {
            contexts = BuildParticipantContexts(speakers);
            int replyCount = Mathf.Clamp(maxTurns - 1, 1, 4);
            if (speakers.Count > 2)
                replyCount = Mathf.Max(replyCount, Mathf.Min(4, speakers.Count - 1));

            List<string> speakerOrder = BuildSpeakerOrder(speakers, intendedPartnerName,
                replyCount, topic, openerLine);
            ConversationScript script = preparedScript;
            if (script == null && brain != null && speakerOrder.Count > 0)
                script = await brain.GenerateConversationAsync(contexts,
                    owner.DisplayName, topic, speakerOrder);
            if (script == null || string.IsNullOrWhiteSpace(script.openingLine))
                return;

            spokenOpening = script.openingLine;
            await DisplayTurnAsync(owner, spokenOpening, speakers);
            conversationSpoken = true;
            onOpeningSpoken?.Invoke();
            owner.ApplyEffects(0f, 0f, 5f, 0f);

            List<ConversationTurn> generated = preparedScript != null
                ? new List<ConversationTurn>(preparedScript.turns)
                : script != null
                    ? MergeGeneratedTurns(script.turns, speakerOrder)
                    : new List<ConversationTurn>();
            if (generated.Count == 0)
                return;

            foreach (ConversationTurn turn in generated)
            {
                AIWorkerAgent speaker = FindSpeaker(speakers, turn?.speaker);
                if (speaker == null || string.IsNullOrWhiteSpace(turn.line))
                    continue;
                completedTurns.Add(turn);
                await DisplayTurnAsync(speaker, turn.line, speakers);
                speaker.ApplyEffects(0f, 0f, 5f, 0f);
                ExtendAll(speakers, 6f, includeOwner: false);
            }
            completedSocialEvents = script?.socialEvents;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(AgentConversationController) +
                " stopped a conversation: " + exception.Message, this);
        }
        finally
        {
            contexts ??= BuildParticipantContexts(speakers);
            if (conversationSpoken)
            {
                string recordedTopic = string.IsNullOrWhiteSpace(topic)
                    ? spokenOpening : topic;
                brain?.RecordConversation(contexts, recordedTopic,
                    owner.DisplayName, spokenOpening,
                    completedTurns, completedSocialEvents);
                RememberConversation(brain, speakers, recordedTopic);
                StrengthenRelationships(speakers, completedSocialEvents, recordedTopic);
            }
            HideAll(speakers);
            SetConversationCooldown(speakers, 2.5f);
            EndConversation(participants);
            ReleaseAfterConversation(speakers, conversationSpoken);
            if (action != null)
                activeConversationActions.Remove(action);
            ownedConversationAction = null;
            if (activeConversationOwner == this)
                activeConversationOwner = null;
        }
    }

    private static List<ConversationParticipantContext> BuildParticipantContexts(
        List<AIWorkerAgent> speakers)
    {
        List<ConversationParticipantContext> result = new();
        foreach (AIWorkerAgent speaker in speakers)
        {
            AgentConversationController controller = GetController(speaker);
            if (controller != null)
                result.Add(controller.BuildParticipantContext());
        }
        return result;
    }

    private List<string> BuildSpeakerOrder(List<AIWorkerAgent> speakers,
        string intendedPartnerName, int replyCount, string topic, string openerLine)
    {
        List<AIWorkerAgent> others = new();
        foreach (AIWorkerAgent speaker in speakers)
            if (speaker != null && speaker != owner)
                others.Add(speaker);

        string addressedName = string.IsNullOrWhiteSpace(intendedPartnerName)
            ? FindAddressedName(openerLine, others)
            : intendedPartnerName;

        others.Sort((left, right) =>
        {
            bool leftTarget = string.Equals(left.DisplayName, addressedName,
                StringComparison.OrdinalIgnoreCase);
            bool rightTarget = string.Equals(right.DisplayName, addressedName,
                StringComparison.OrdinalIgnoreCase);
            if (leftTarget != rightTarget)
                return leftTarget ? -1 : 1;
            int relevance = right.ScoreConversationTopic(topic).CompareTo(left.ScoreConversationTopic(topic));
            return relevance != 0 ? relevance : left.GetInstanceID().CompareTo(right.GetInstanceID());
        });

        List<AIWorkerAgent> ordered = new();
        foreach (AIWorkerAgent other in others)
        {
            if (ordered.Count >= replyCount)
                break;
            ordered.Add(other);
        }

        int cycle = 0;
        while (ordered.Count < replyCount && speakers.Count > 0)
        {
            AIWorkerAgent next = speakers[cycle++ % speakers.Count];
            if (ordered.Count > 0 && ordered[ordered.Count - 1] == next)
                next = speakers[cycle++ % speakers.Count];
            ordered.Add(next);
        }

        List<string> names = new();
        foreach (AIWorkerAgent speaker in ordered)
            names.Add(speaker.DisplayName);
        return names;
    }

    private static List<ConversationTurn> MergeGeneratedTurns(
        List<ConversationTurn> generated, List<string> speakerOrder)
    {
        List<ConversationTurn> merged = new();
        for (int i = 0; i < speakerOrder.Count; i++)
        {
            ConversationTurn candidate = generated != null && i < generated.Count
                ? generated[i] : null;
            bool valid = candidate != null
                && !string.IsNullOrWhiteSpace(candidate.line)
                && string.Equals(candidate.speaker, speakerOrder[i],
                    StringComparison.OrdinalIgnoreCase);
            if (valid)
                merged.Add(candidate);
        }
        return merged;
    }

    private static string FindAddressedName(string openerLine, List<AIWorkerAgent> candidates)
    {
        if (string.IsNullOrWhiteSpace(openerLine) || candidates == null)
            return "";

        string normalized = openerLine.TrimStart();
        foreach (AIWorkerAgent candidate in candidates)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.DisplayName))
                continue;

            string name = candidate.DisplayName.Trim();
            if (normalized.StartsWith(name + ",", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                return candidate.DisplayName;
        }

        return "";
    }

    private static AIWorkerAgent FindSpeaker(List<AIWorkerAgent> speakers, string displayName)
    {
        if (speakers == null || string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (AIWorkerAgent speaker in speakers)
            if (speaker != null && string.Equals(speaker.DisplayName, displayName,
                    StringComparison.OrdinalIgnoreCase))
                return speaker;
        return null;
    }

    private static void StrengthenRelationships(List<AIWorkerAgent> speakers,
        List<SocialMemoryEntry> socialEvents, string topic)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        for (int i = 0; i < speakers.Count; i++)
        {
            if (speakers[i] == null)
                continue;
            for (int j = i + 1; j < speakers.Count; j++)
            {
                if (speakers[j] == null)
                    continue;
                GetController(speakers[i])?.IncrementAffinity(speakers[j].DisplayName);
                GetController(speakers[j])?.IncrementAffinity(speakers[i].DisplayName);
                brain?.RecordRelationshipInteraction(
                    speakers[i].AgentId, speakers[j].AgentId, "conversation", topic);
            }
        }

        if (brain == null || socialEvents == null)
            return;
        foreach (SocialMemoryEntry socialEvent in socialEvents)
        {
            AIWorkerAgent source = FindSpeaker(speakers, socialEvent?.sourceAgent);
            AIWorkerAgent target = FindSpeaker(speakers, socialEvent?.targetAgent);
            if (source != null && target != null)
                brain.RecordRelationshipInteraction(source.AgentId, target.AgentId,
                    socialEvent.type, socialEvent.subject);
        }
    }

    private static bool ContainsSpeaker(List<AIWorkerAgent> speakers, string displayName)
    {
        if (speakers == null || string.IsNullOrWhiteSpace(displayName))
            return false;

        foreach (AIWorkerAgent speaker in speakers)
            if (speaker != null && string.Equals(speaker.DisplayName, displayName,
                StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private async Task DisplayTurnAsync(AIWorkerAgent speaker, string line,
        List<AIWorkerAgent> speakers)
    {
        Debug.Log("[Chat] " + speaker.DisplayName + ": " + line, speaker);

        OrientTowardSpeaker(speakers, speaker);
        HideAll(speakers);
        speaker.ShowSpeech(speaker.DisplayName, line, GetSpeakerColor(speaker));

        float seconds = Mathf.Clamp(2.4f + line.Length * 0.095f, minimumLineSeconds, maximumLineSeconds);
        await Task.Delay(Mathf.RoundToInt(seconds * 1000f));
        speaker.HideSpeech();
        if (betweenTurnsSeconds > 0f)
            await Task.Delay(Mathf.RoundToInt(betweenTurnsSeconds * 1000f));
    }

    private static void OrientTowardSpeaker(List<AIWorkerAgent> speakers,
        AIWorkerAgent activeSpeaker)
    {
        if (activeSpeaker == null || speakers == null)
            return;

        Vector2 listenerCenter = Vector2.zero;
        int listenerCount = 0;
        foreach (AIWorkerAgent participant in speakers)
        {
            if (participant == null || participant == activeSpeaker)
                continue;
            participant.FaceToward(activeSpeaker.GetPosition());
            listenerCenter += participant.GetPosition();
            listenerCount++;
        }

        if (listenerCount > 0)
            activeSpeaker.FaceToward(listenerCenter / listenerCount);
    }

    private static Color GetSpeakerColor(AIWorkerAgent speaker)
    {
        Color32[] palette =
        {
            new(47, 111, 237, 255),
            new(196, 60, 99, 255),
            new(47, 133, 90, 255),
            new(124, 58, 237, 255),
            new(180, 83, 9, 255),
            new(15, 118, 110, 255)
        };

        string id = speaker != null ? speaker.AgentId : string.Empty;
        int hash = 17;
        foreach (char character in id)
            hash = unchecked(hash * 31 + character);
        int index = (hash & int.MaxValue) % palette.Length;
        return palette[index];
    }

    private static void HideAll(List<AIWorkerAgent> speakers)
    {
        foreach (AIWorkerAgent speaker in speakers)
            if (speaker != null)
            {
                speaker.HideThought();
                speaker.HideSpeech();
            }
    }

    private static void RememberConversation(LLMBrainService brain,
        List<AIWorkerAgent> speakers, string topic)
    {
        if (brain == null || string.IsNullOrWhiteSpace(topic))
            return;

        foreach (AIWorkerAgent listener in speakers)
        {
            if (listener == null)
                continue;
            StringBuilder others = new();
            foreach (AIWorkerAgent speaker in speakers)
            {
                if (speaker == null || speaker == listener)
                    continue;
                if (others.Length > 0)
                    others.Append(", ");
                others.Append(speaker.DisplayName);
            }
            brain.Remember(listener.AgentId, "Talked with " + others + " about " + topic.Trim() + ".");
        }
    }

    private List<AIWorkerAgent> FindNearbyParticipants(OfficeActionPoint action, float radius)
    {
        List<AIWorkerAgent> result = new();
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null)
            return result;

        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker == null || worker == owner)
                continue;

            if (!worker.IsActingAt(action))
                continue;

            AgentConversationController conversation = GetController(worker);
            if (conversation != null && conversation.inConversation)
                continue;

            if (worker.IsActingAt(action)
                || Vector2.Distance(owner.GetPosition(), worker.GetPosition()) <= radius)
                result.Add(worker);
        }

        return result;
    }

    private void AddLateParticipants(OfficeActionPoint action,
        List<AIWorkerAgent> participants, List<AIWorkerAgent> speakers)
    {
        List<AIWorkerAgent> nearby = FindNearbyParticipants(action, participantRadius);
        foreach (AIWorkerAgent worker in nearby)
        {
            if (worker == null || speakers.Contains(worker))
                continue;

            participants.Add(worker);
            speakers.Add(worker);
            worker.ClearConversationDirective();
            SetConversationState(worker, true);
            worker.ExtendActing(30f);
            worker.HideThought();
        }
    }

    private List<AIWorkerAgent> SelectConversationPair(
        List<AIWorkerAgent> participants, string intendedPartnerName)
    {
        List<AIWorkerAgent> result = new();
        if (participants == null)
            return result;

        AIWorkerAgent selected = null;
        if (!string.IsNullOrWhiteSpace(intendedPartnerName))
            selected = participants.Find(worker => worker != null
                && string.Equals(worker.DisplayName, intendedPartnerName,
                    StringComparison.OrdinalIgnoreCase));

        if (selected == null)
        {
            float nearestDistance = float.MaxValue;
            foreach (AIWorkerAgent worker in participants)
            {
                if (worker == null)
                    continue;
                float distance = Vector2.Distance(owner.GetPosition(), worker.GetPosition());
                if (distance >= nearestDistance)
                    continue;
                nearestDistance = distance;
                selected = worker;
            }
        }

        if (selected != null)
            result.Add(selected);
        return result;
    }

    private bool HasUnrelatedConversationNearby(List<AIWorkerAgent> participants)
    {
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null)
            return false;

        float radiusSquared = conversationSeparationRadius * conversationSeparationRadius;
        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker == null || worker == owner
                || (participants != null && participants.Contains(worker)))
                continue;
            AgentConversationController controller = GetController(worker);
            if (controller == null || !controller.inConversation)
                continue;

            if ((worker.GetPosition() - owner.GetPosition()).sqrMagnitude < radiusSquared)
                return true;
            if (participants == null)
                continue;
            foreach (AIWorkerAgent participant in participants)
                if (participant != null
                    && (worker.GetPosition() - participant.GetPosition()).sqrMagnitude
                        < radiusSquared)
                    return true;
        }
        return false;
    }

    private void IncrementAffinity(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return;

        affinity[displayName] = affinity.TryGetValue(displayName, out int value) ? value + 1 : 1;
    }

    private void EndConversation(List<AIWorkerAgent> participants)
    {
        SetConversationState(owner, false);
        if (participants == null)
            return;

        foreach (AIWorkerAgent participant in participants)
            SetConversationState(participant, false);
    }

    private static void SetConversationState(AIWorkerAgent agent, bool value)
    {
        AgentConversationController controller = GetController(agent);
        if (controller != null)
            controller.inConversation = value;
    }

    private static AgentConversationController GetController(AIWorkerAgent agent)
    {
        if (agent != null && agent.TryGetComponent(out AgentConversationController controller))
            return controller;

        return null;
    }

    private void ExtendAll(List<AIWorkerAgent> participants, float seconds, bool includeOwner = true)
    {
        if (includeOwner)
            owner.ExtendActing(seconds);

        foreach (AIWorkerAgent participant in participants)
            if (participant != null)
                participant.ExtendActing(seconds);
    }

    private static void SetConversationCooldown(List<AIWorkerAgent> speakers, float seconds)
    {
        foreach (AIWorkerAgent speaker in speakers)
            GetController(speaker)?.SetCooldown(seconds);
    }

    private static void ReleaseAfterConversation(List<AIWorkerAgent> speakers,
        bool conversationSucceeded)
    {
        if (speakers == null)
            return;

        for (int i = 0; i < speakers.Count; i++)
        {
            AIWorkerAgent speaker = speakers[i];
            if (speaker == null)
                continue;

            bool leaveSoon = speakers.Count <= 2
                ? i == 0 || UnityEngine.Random.value < 0.65f
                : UnityEngine.Random.value < 0.45f;
            float linger = leaveSoon
                ? UnityEngine.Random.Range(0.3f, 1.2f)
                : UnityEngine.Random.Range(4f, 8f);
            speaker.CompleteConversationActivity(conversationSucceeded);
            speaker.EndSocialConversation(leaveSoon, linger);
        }
    }

    private static string BuildParticipantNames(List<AIWorkerAgent> speakers)
    {
        string result = "";
        foreach (AIWorkerAgent speaker in speakers)
        {
            if (speaker == null)
                continue;

            result += (result.Length > 0 ? ", " : "") + speaker.DisplayName;
        }

        return result;
    }

    public static bool IsSocialSpot(OfficeActionType type)
    {
        return IsRoutineSocialSpot(type)
            || type == OfficeActionType.Printer
            || type == OfficeActionType.Whiteboard;
    }

    public static bool IsRoutineSocialSpot(OfficeActionType type)
    {
        return type == OfficeActionType.ChatSpot
            || type == OfficeActionType.BreakSpot
            || type == OfficeActionType.MeetingRoom;
    }
}

