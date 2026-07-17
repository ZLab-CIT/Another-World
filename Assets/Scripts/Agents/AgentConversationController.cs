using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

[RequireComponent(typeof(AIWorkerAgent))]
public class AgentConversationController : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float participantRadius = 1.5f;
    [SerializeField, Min(1)] private int maxTurns = 4;
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
        AIWorkerAgent partner = alreadyThere.Count > 0
            ? alreadyThere[UnityEngine.Random.Range(0, alreadyThere.Count)]
            : affinity.Count > 0 && UnityEngine.Random.value < 0.55f
                ? candidates[0]
                : candidates[UnityEngine.Random.Range(0, candidates.Count)];
        LLMBrainService brain = LLMBrainService.Instance;
        int starterCount = Mathf.Max(1, owner.ConversationStarterCount);
        string opener = null;
        for (int attempt = 0; attempt < starterCount; attempt++)
        {
            string candidate = owner.GetConversationStarter(partner.DisplayName, localStarterVariation++);
            if (brain == null || brain.IsFreshOpening(owner.AgentId, candidate))
            {
                opener = candidate;
                break;
            }
        }
        opener ??= owner.GetConversationStarter(partner.DisplayName, localStarterVariation++);

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
        string line, string topic, string intendedPartnerName)
    {
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
            return;
        }

        RunConversation(action, participants, line, topic, intendedPartnerName);
    }

    private void BeginGeneratedConversation(OfficeActionPoint action,
        List<AIWorkerAgent> participants, ConversationIntent intent)
    {
        if (intent == null || string.IsNullOrWhiteSpace(intent.openingLine))
            return;

        string intendedPartner = intent.intendedPartnerName ?? "";
        string opener = intent.openingLine.Trim();
        string topic = string.IsNullOrWhiteSpace(intent.topic) ? opener : intent.topic.Trim();

        string source = intent.generatedByModel ? "AI plan" : "local plan";
        Debug.Log("[Conversation topic · " + source + "] " + owner.DisplayName
            + (string.IsNullOrWhiteSpace(intendedPartner) ? "" : " → " + intendedPartner)
            + ": " + opener, owner);

        BeginConversation(action, participants, opener, topic, intendedPartner);
    }

    private async void RunConversation(OfficeActionPoint action, List<AIWorkerAgent> participants,
        string openerLine, string topic, string intendedPartnerName)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        List<AIWorkerAgent> speakers = new() { owner };
        foreach (AIWorkerAgent participant in participants)
            if (participant != null && participant != owner)
                speakers.Add(participant);

        List<ConversationParticipantContext> contexts = BuildParticipantContexts(speakers);
        List<string> speakerOrder = BuildSpeakerOrder(speakers, intendedPartnerName,
            Mathf.Clamp(maxTurns - 1, 1, 3), topic);
        Task<List<ConversationTurn>> scriptTask = brain != null && speakerOrder.Count > 0
            ? brain.GenerateConversationAsync(contexts, owner.DisplayName, openerLine, topic, speakerOrder)
            : null;

        activeGroupDialogue = CreateGroupDialogue(action);
        AgentThoughtBubble groupDialogue = activeGroupDialogue;
        HideAll(speakers);
        List<ConversationTurn> completedTurns = new();

        try
        {
            await DisplayTurnAsync(owner, openerLine, speakers, groupDialogue);
            owner.ApplyEffects(0f, 0f, 5f, 0f);

            List<ConversationTurn> generated = scriptTask != null ? await scriptTask : null;
            if (generated == null)
            {
                Debug.LogWarning("[Conversation ended] No coherent continuation was generated for " +
                    BuildParticipantNames(speakers) + ".", this);
                return;
            }

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
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(AgentConversationController) +
                " stopped a conversation: " + exception.Message, this);
        }
        finally
        {
            brain?.RecordConversation(contexts, topic, owner.DisplayName, openerLine, completedTurns);
            RememberConversation(brain, speakers, topic);
            StrengthenRelationships(speakers);
            HideAll(speakers);
            SetConversationCooldown(speakers, 12f);
            EndConversation(participants);
            if (groupDialogue != null)
            {
                groupDialogue.Hide();
                Destroy(groupDialogue.gameObject);
            }
            if (activeGroupDialogue == groupDialogue)
                activeGroupDialogue = null;
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
        string intendedPartnerName, int replyCount, string topic)
    {
        List<AIWorkerAgent> others = new();
        foreach (AIWorkerAgent speaker in speakers)
            if (speaker != null && speaker != owner)
                others.Add(speaker);

        others.Sort((left, right) =>
        {
            bool leftTarget = string.Equals(left.DisplayName, intendedPartnerName,
                StringComparison.OrdinalIgnoreCase);
            bool rightTarget = string.Equals(right.DisplayName, intendedPartnerName,
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
        while (ordered.Count < replyCount && others.Count > 0)
        {
            AIWorkerAgent next = ordered.Count == 0 || ordered[ordered.Count - 1] != owner
                ? owner
                : others[cycle++ % others.Count];
            if (ordered.Count > 0 && ordered[ordered.Count - 1] == next)
                next = others[cycle++ % others.Count];
            ordered.Add(next);
        }

        List<string> names = new();
        foreach (AIWorkerAgent speaker in ordered)
            names.Add(speaker.DisplayName);
        return names;
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
        Debug.Log("[Conversation: " + BuildParticipantNames(speakers) + "] " +
            speaker.DisplayName + ": " + line, speaker);
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

            if (Vector2.Distance(owner.GetPosition(), worker.GetPosition()) <= radius)
                result.Add(worker);
        }

        return result;
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
