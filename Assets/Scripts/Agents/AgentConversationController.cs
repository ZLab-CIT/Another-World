using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

[RequireComponent(typeof(AIWorkerAgent))]
public class AgentConversationController : MonoBehaviour
{
    private static readonly HashSet<OfficeActionPoint> activeConversationActions = new();
    private static readonly Dictionary<string, Queue<string>> recentLocalOpeners = new();
    private static readonly Queue<string> recentFallbackReplies = new();
    private const int RecentLocalOpenerLimit = 12;
    private const int RecentFallbackReplyLimit = 24;

    [SerializeField, Min(0.1f)] private float participantRadius = 1.5f;
    [SerializeField, Min(1)] private int maxTurns = 7;
    [SerializeField, Min(0.5f)] private float minimumLineSeconds = 2.2f;
    [SerializeField, Min(0.5f)] private float maximumLineSeconds = 4.5f;
    [SerializeField, Min(0f)] private float betweenTurnsSeconds = 0.2f;

    private readonly Dictionary<string, int> affinity = new();
    private AIWorkerAgent owner;
    private float nextSocialCheckTime;
    private bool inConversation;
    private int localStarterVariation;
    private AgentThoughtBubble activeGroupDialogue;
    public bool IsInConversation => inConversation;
    public bool IsSociallyCoolingDown => Time.time < nextSocialCheckTime;

    public static bool IsConversationActiveAt(OfficeActionPoint action)
    {
        return action != null && activeConversationActions.Contains(action);
    }

    private void Awake()
    {
        owner = GetComponent<AIWorkerAgent>();
        localStarterVariation = UnityEngine.Random.Range(0, 1000);
    }

    private void OnDisable()
    {
        inConversation = false;
        owner?.HideThought();
        if (activeGroupDialogue != null)
        {
            Destroy(activeGroupDialogue.gameObject);
            activeGroupDialogue = null;
        }
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
            relationships = BuildRelationships()
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
            string groupOpener = CreateGroupConversationOpener(localStarterVariation++);
            return new ConversationIntent
            {
                initiatorAgentId = owner.AgentId,
                initiatorName = owner.DisplayName,
                intendedPartnerName = "",
                topic = groupOpener,
                openingLine = groupOpener,
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
        string opener = ChooseLocalOpener(partner.DisplayName);

        return new ConversationIntent
        {
            initiatorAgentId = owner.AgentId,
            initiatorName = owner.DisplayName,
            intendedPartnerName = partner.DisplayName,
            topic = opener,
            openingLine = opener,
            actionType = action.actionType,
            createdAt = Time.time,
            expiresAt = expiresAt
        };
    }

    private string ChooseLocalOpener(string partnerName)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        List<string> candidates = BuildLocalOpenerCandidates(partnerName);
        Shuffle(candidates);

        string fallback = null;
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string opener = candidate.Trim();
            fallback ??= opener;
            if (WasRecentlyUsedLocalOpener(owner.AgentId, opener))
                continue;
            if (brain != null && !brain.IsFreshOpening(owner.AgentId, opener))
                continue;

            RememberLocalOpener(owner.AgentId, opener);
            return opener;
        }

        string contextual = CreateContextualStarter(partnerName, localStarterVariation++);
        string chosen = fallback != null && UnityEngine.Random.value < 0.35f ? fallback : contextual;
        RememberLocalOpener(owner.AgentId, chosen);
        return chosen;
    }

    private List<string> BuildLocalOpenerCandidates(string partnerName)
    {
        List<string> candidates = new();
        int starterCount = owner.ConversationStarterCount;
        for (int i = 0; i < starterCount; i++)
            candidates.Add(owner.GetConversationStarter(partnerName, i));

        int seed = localStarterVariation + UnityEngine.Random.Range(0, 1000);
        for (int i = 0; i < 10; i++)
            candidates.Add(CreateContextualStarter(partnerName, seed + i));

        localStarterVariation += UnityEngine.Random.Range(3, 17);
        return candidates;
    }

    private static void Shuffle(List<string> values)
    {
        if (values == null)
            return;

        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static bool WasRecentlyUsedLocalOpener(string agentId, string opener)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(opener))
            return false;
        if (!recentLocalOpeners.TryGetValue(agentId, out Queue<string> recent))
            return false;

        string normalized = NormalizeLocalOpener(opener);
        foreach (string value in recent)
            if (value == normalized)
                return true;
        return false;
    }

    private static void RememberLocalOpener(string agentId, string opener)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(opener))
            return;

        if (!recentLocalOpeners.TryGetValue(agentId, out Queue<string> recent))
        {
            recent = new Queue<string>();
            recentLocalOpeners[agentId] = recent;
        }

        recent.Enqueue(NormalizeLocalOpener(opener));
        while (recent.Count > RecentLocalOpenerLimit)
            recent.Dequeue();
    }

    private static string NormalizeLocalOpener(string opener)
    {
        return opener.Trim().ToLowerInvariant();
    }

    private static string CreateGroupConversationOpener(int variation)
    {
        switch (Mathf.Abs(variation) % 6)
        {
            case 0:
                return "Guys, what do you think makes a small office ritual actually worth keeping?";
            case 1:
                return "Everyone, be honest, which tiny work habit secretly saves your whole day?";
            case 2:
                return "Guys, what would you change here if nobody could say no for one afternoon?";
            case 3:
                return "Everyone, what is the most underrated way to make a rough day easier?";
            case 4:
                return "Guys, which harmless office debate are you surprisingly willing to defend?";
            default:
                return "Everyone, what is one small thing here that feels oddly important?";
        }
    }

    private string CreateContextualStarter(string partnerName, int variation)
    {
        switch (Mathf.Abs(variation) % 16)
        {
            case 0:
                return partnerName + ", I need a second opinion before this thought becomes my whole afternoon.";
            case 1:
                return partnerName + ", what is one tiny thing here you would improve first?";
            case 2:
                return partnerName + ", I just remembered something oddly specific and now I need your reaction.";
            case 3:
                return partnerName + ", which small office mystery deserves an investigation today?";
            case 4:
                return partnerName + ", tell me if this is useful thinking or just break-time nonsense.";
            case 5:
                return partnerName + ", what would make today feel less repetitive for you?";
            case 6:
                return partnerName + ", I have a harmless question with surprisingly strong opinions attached.";
            case 7:
                return partnerName + ", what is the most interesting thing you noticed today?";
            case 8:
                return partnerName + ", I am trying to decide whether this is a good idea or just a confident one.";
            case 9:
                return partnerName + ", what is something small here that people underestimate?";
            case 10:
                return partnerName + ", I want your honest answer before I overthink this.";
            case 11:
                return partnerName + ", what would you defend in this office even if everyone disagreed?";
            case 12:
                return partnerName + ", I need a reality check on a thought I just had.";
            case 13:
                return partnerName + ", what is one thing today that deserves more attention?";
            case 14:
                return partnerName + ", I have a question that sounds casual but might reveal too much.";
            default:
                return partnerName + ", what would make this break more interesting?";
        }
    }

    private float PartnerScore(AIWorkerAgent candidate, OfficeActionPoint action)
    {
        if (candidate == null)
            return float.MinValue;
        float score = affinity.TryGetValue(candidate.DisplayName, out int closeness) ? closeness * 4f : 0f;
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
        if (brain == null || !brain.EnableSocialReplies)
            return;

        List<AIWorkerAgent> speakers = new() { owner };
        speakers.AddRange(participants);
        AIWorkerAgent starter = SelectConversationStarter(action, speakers, out ConversationIntent intent);
        if (starter != owner)
            return;

        BeginGeneratedConversation(action, participants, intent);
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

    private void BeginConversation(OfficeActionPoint action, List<AIWorkerAgent> participants,
        string line, string topic, string intendedPartnerName, Action onOpeningSpoken = null)
    {
        if (action != null && activeConversationActions.Contains(action))
            return;
        if (action != null)
            activeConversationActions.Add(action);

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
            return;
        }

        RunConversation(action, participants, line, topic, intendedPartnerName, onOpeningSpoken);
    }

    private void BeginGeneratedConversation(OfficeActionPoint action,
        List<AIWorkerAgent> participants, ConversationIntent intent)
    {
        if (intent == null || string.IsNullOrWhiteSpace(intent.openingLine))
            return;

        string intendedPartner = intent.intendedPartnerName ?? "";
        string opener = intent.openingLine.Trim();
        string topic = string.IsNullOrWhiteSpace(intent.topic) ? opener : intent.topic.Trim();

        BeginConversation(action, participants, opener, topic, intendedPartner, intent.onOpeningSpoken);
    }

    public bool BeginDirectConversation(AIWorkerAgent target, string openingLine, string topic)
    {
        AgentConversationController targetConversation = GetController(target);
        if (target == null || target == owner || inConversation
            || targetConversation == null || targetConversation.inConversation
            || string.IsNullOrWhiteSpace(openingLine))
            return false;

        BeginConversation(null, new List<AIWorkerAgent> { target }, openingLine,
            string.IsNullOrWhiteSpace(topic) ? openingLine : topic,
            target.DisplayName);
        return true;
    }

    private async void RunConversation(OfficeActionPoint action, List<AIWorkerAgent> participants,
        string openerLine, string topic, string intendedPartnerName, Action onOpeningSpoken)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        List<AIWorkerAgent> speakers = new() { owner };
        foreach (AIWorkerAgent participant in participants)
            if (participant != null && participant != owner)
                speakers.Add(participant);

        activeGroupDialogue = CreateGroupDialogue(action);
        AgentThoughtBubble groupDialogue = activeGroupDialogue;
        HideAll(speakers);
        List<ConversationTurn> completedTurns = new();
        List<SocialMemoryEntry> completedSocialEvents = null;
        List<ConversationParticipantContext> contexts = null;

        try
        {
            await DisplayTurnAsync(owner, openerLine, speakers, groupDialogue);
            onOpeningSpoken?.Invoke();
            owner.ApplyEffects(0f, 0f, 5f, 0f);

            AddLateParticipants(action, participants, speakers);
            contexts = BuildParticipantContexts(speakers);
            int replyCount = Mathf.Clamp(maxTurns - 1, 1, 6);
            if (speakers.Count > 2)
                replyCount = Mathf.Max(replyCount, Mathf.Min(6, speakers.Count - 1));

            List<string> speakerOrder = BuildSpeakerOrder(speakers, intendedPartnerName,
                replyCount, topic, openerLine);
            Task<ConversationScript> scriptTask = brain != null && speakerOrder.Count > 0
                ? brain.GenerateConversationAsync(contexts, owner.DisplayName, openerLine, topic, speakerOrder)
                : null;
            ConversationScript script = scriptTask != null ? await scriptTask : null;
            List<ConversationTurn> generated = script?.turns;
            if (generated == null)
                generated = BuildFallbackTurns(speakerOrder, openerLine, topic);
            else if (generated.Count < speakerOrder.Count)
                CompletePartialTurns(generated, speakerOrder, owner.DisplayName, topic);

            foreach (ConversationTurn turn in generated)
            {
                AIWorkerAgent speaker = FindSpeaker(speakers, turn?.speaker);
                if (speaker == null || string.IsNullOrWhiteSpace(turn.line))
                    continue;
                completedTurns.Add(turn);
                await DisplayTurnAsync(speaker, turn.line, speakers, groupDialogue);
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
            brain?.RecordConversation(contexts, topic, owner.DisplayName, openerLine,
                completedTurns, completedSocialEvents);
            RememberConversation(brain, speakers, topic);
            StrengthenRelationships(speakers);
            HideAll(speakers);
            SetConversationCooldown(speakers, 2.5f);
            EndConversation(participants);
            ReleaseAfterConversation(speakers);
            if (groupDialogue != null)
            {
                groupDialogue.Hide();
                Destroy(groupDialogue.gameObject);
            }
            if (activeGroupDialogue == groupDialogue)
                activeGroupDialogue = null;
            if (action != null)
                activeConversationActions.Remove(action);
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

    private static List<ConversationTurn> BuildFallbackTurns(
        List<string> speakerOrder, string openerLine, string topic)
    {
        List<ConversationTurn> turns = new();
        if (speakerOrder == null)
            return turns;

        string subject = ShortTopic(string.IsNullOrWhiteSpace(topic) ? openerLine : topic);
        bool birthday = IsBirthdayTopic(openerLine) || IsBirthdayTopic(topic);
        bool hatGift = ContainsIgnoreCase(openerLine, "hat") || ContainsIgnoreCase(topic, "hat");
        bool snackGift = ContainsIgnoreCase(openerLine, "snack") || ContainsIgnoreCase(topic, "snack");
        if (birthday)
            return BuildBirthdayFallbackTurns(speakerOrder, subject, hatGift, snackGift);

        string[] templates =
        {
            "My answer changes if this affects tomorrow's work.",
            "I would test the smallest version before anyone gets attached.",
            "That sounds easy until the second person has an opinion.",
            "I like the idea more if it does not create hidden chores.",
            "I can answer, but my first instinct is probably too blunt.",
            "Give me the version with consequences, not the polite version.",
            "That sounds like one of those choices that gets weird later.",
            "My answer depends on who has to maintain it afterward.",
            "I would ask who benefits before I defend the idea.",
            "The practical answer and the fun answer are not the same.",
            "I need one concrete example before I trust my reaction.",
            "That feels useful, but only if people actually notice it."
        };

        int offset = ConversationTemplateOffset(subject, speakerOrder);
        for (int i = 0; i < speakerOrder.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(speakerOrder[i]))
                continue;
            turns.Add(new ConversationTurn
            {
                speaker = speakerOrder[i],
                line = PickFallbackReply(templates, i + offset)
            });
        }

        return turns;
    }

    private static void CompletePartialTurns(List<ConversationTurn> turns,
        List<string> speakerOrder, string initiatingSpeaker, string topic)
    {
        if (turns == null || speakerOrder == null)
            return;
        while (turns.Count < speakerOrder.Count)
        {
            int index = turns.Count;
            string speaker = speakerOrder[index];
            bool initiator = string.Equals(speaker, initiatingSpeaker,
                StringComparison.OrdinalIgnoreCase);
            string line = BuildPartialReply(speaker, initiator, index, topic);
            turns.Add(new ConversationTurn { speaker = speaker, line = line });
        }
    }

    private static string BuildPartialReply(string speaker, bool initiator, int index, string topic)
    {
        if (IsBirthdayTopic(topic))
        {
            bool birthdayPerson = !string.IsNullOrWhiteSpace(speaker)
                && ContainsIgnoreCase(topic, speaker);
            if (birthdayPerson && !initiator)
                return "Thank you. I am glad you came.";
            return initiator
                ? "I hope you get time to enjoy today."
                : "I hope today gives you something fun.";
        }

        if (ContainsIgnoreCase(topic, "coffee") || ContainsIgnoreCase(topic, "drink"))
            return initiator
                ? "I chose one I thought you would like."
                : "Thank you. I will try it now.";

        if (ContainsIgnoreCase(topic, "advice") || ContainsIgnoreCase(topic, "opinion"))
            return initiator
                ? "I am not sure which choice would work best."
                : "What have you tried so far?";

        if (initiator)
            return "The hardest part is deciding what to do next.";
        return index % 2 == 0
            ? "What makes that important right now?"
            : "Tell me one part you want to change.";
    }

    private static string SpokenSubject(string topic)
    {
        string subject = string.IsNullOrWhiteSpace(topic) ? "this" : topic.Trim().TrimEnd('.', '!', '?');
        string[] prefixes =
        {
            "ask for advice on ", "ask advice about ", "discuss ", "check ",
            "talk about ", "get advice on "
        };
        foreach (string prefix in prefixes)
            if (subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                subject = subject.Substring(prefix.Length).Trim();
                break;
            }
        return string.IsNullOrWhiteSpace(subject) ? "this" : subject;
    }

    private static List<ConversationTurn> BuildBirthdayFallbackTurns(
        List<string> speakerOrder, string subject, bool hatGift, bool snackGift)
    {
        List<ConversationTurn> turns = new();
        string giftReaction = hatGift
            ? "The hat is perfect. I am absolutely wearing it today."
            : snackGift
                ? "The snack is perfect timing. Thank you, seriously."
                : "I did not expect everyone to remember. Thank you.";
        string[] templates =
        {
            giftReaction,
            "We remembered because " + subject + " is not a normal workday.",
            "Make one birthday wish before someone turns this into a meeting.",
            "I vote we celebrate properly after the urgent work is done.",
            "No repeat speeches from me, but I hope this year treats you well.",
            "Someone should take a photo before the office gets chaotic again.",
            "This office is bad at surprises, so please act surprised for us.",
            "I hope the next year gives you fewer emergencies and better snacks.",
            "You get one birthday veto over our worst conversation topic today.",
            "I am saving the sentimental speech for when nobody can quote me."
        };

        for (int i = 0; i < speakerOrder.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(speakerOrder[i]))
                continue;
            turns.Add(new ConversationTurn
            {
                speaker = speakerOrder[i],
                // BuildSpeakerOrder puts the birthday person first.
                line = i == 0 ? giftReaction : PickFallbackReply(templates, i + 2)
            });
        }

        return turns;
    }

    private static string PickFallbackReply(string[] templates, int preferredIndex)
    {
        if (templates == null || templates.Length == 0)
            return "";

        for (int attempt = 0; attempt < templates.Length; attempt++)
        {
            string candidate = templates[Mathf.Abs(preferredIndex + attempt) % templates.Length];
            if (!WasRecentlyUsedFallbackReply(candidate))
            {
                RememberFallbackReply(candidate);
                return candidate;
            }
        }

        string fallback = templates[Mathf.Abs(preferredIndex) % templates.Length];
        RememberFallbackReply(fallback);
        return fallback;
    }

    private static bool WasRecentlyUsedFallbackReply(string line)
    {
        string normalized = NormalizeLocalOpener(line);
        foreach (string recent in recentFallbackReplies)
            if (recent == normalized)
                return true;
        return false;
    }

    private static void RememberFallbackReply(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        recentFallbackReplies.Enqueue(NormalizeLocalOpener(line));
        while (recentFallbackReplies.Count > RecentFallbackReplyLimit)
            recentFallbackReplies.Dequeue();
    }

    private static int ConversationTemplateOffset(string subject, List<string> speakerOrder)
    {
        unchecked
        {
            int hash = subject != null ? subject.GetHashCode() : 17;
            if (speakerOrder != null)
                foreach (string speaker in speakerOrder)
                    hash = hash * 31 + (speaker != null ? speaker.GetHashCode() : 0);
            hash = hash * 31 + UnityEngine.Random.Range(0, 997);
            return Mathf.Abs(hash);
        }
    }

    private static bool IsBirthdayTopic(string value)
    {
        return ContainsIgnoreCase(value, "birthday") || ContainsIgnoreCase(value, "happy birthday");
    }

    private static bool ContainsIgnoreCase(string value, string fragment)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ShortTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return "that";

        string value = topic.Trim().Trim('"', '\'', '.', '!', '?');
        string[] words = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 6)
            return value.ToLowerInvariant();

        StringBuilder result = new();
        for (int i = 0; i < 6; i++)
        {
            if (result.Length > 0)
                result.Append(' ');
            result.Append(words[i].ToLowerInvariant());
        }
        return result.ToString();
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

    private static void StrengthenRelationships(List<AIWorkerAgent> speakers)
    {
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
            }
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
        List<AIWorkerAgent> speakers, AgentThoughtBubble groupDialogue)
    {
        Debug.Log("[Chat] " + speaker.DisplayName + ": " + line, speaker);

        if (groupDialogue != null)
            groupDialogue.ShowDialogue(speaker.DisplayName, line, GetSpeakerColor(speaker));
        else
        {
            HideAll(speakers);
            speaker.ShowThought($"{speaker.DisplayName}\n{line}");
        }

        float seconds = Mathf.Clamp(1.4f + line.Length * 0.055f, minimumLineSeconds, maximumLineSeconds);
        await Task.Delay(Mathf.RoundToInt(seconds * 1000f));
        if (groupDialogue == null)
            speaker.HideThought();
        if (betweenTurnsSeconds > 0f)
            await Task.Delay(Mathf.RoundToInt(betweenTurnsSeconds * 1000f));
    }

    private AgentThoughtBubble CreateGroupDialogue(OfficeActionPoint action)
    {
        if (action == null || !owner.TryGetComponent(out AgentPresentation2D presentation))
            return null;

        return presentation.CreateSharedDialogueBubble(action.transform);
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
                speaker.HideThought();
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

    private static void ReleaseAfterConversation(List<AIWorkerAgent> speakers)
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
        return type == OfficeActionType.ChatSpot || type == OfficeActionType.BreakSpot;
    }
}

