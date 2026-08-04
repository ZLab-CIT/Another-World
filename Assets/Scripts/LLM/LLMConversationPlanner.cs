using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class LLMConversationPlanner
{
    private readonly LLMBrainService brain;

    public LLMConversationPlanner(LLMBrainService brain)
    {
        this.brain = brain;
    }

    public async Task<ConversationScript> GenerateConversationAsync(
        List<ConversationParticipantContext> participants,
        string openingSpeaker,
        string topic,
        List<string> speakerOrder,
        float temperature,
        int requestTimeoutSeconds,
        int conversationScriptTimeoutSeconds)
    {
        if (participants == null || participants.Count < 2
            || speakerOrder == null || speakerOrder.Count == 0)
            return null;
        ConversationParticipantContext initiator =
            TextUtils.FindParticipant(participants, openingSpeaker);
        ILLMBackend activeBackend = brain.GetBackendForConversation(
            participants, initiator?.agentId);
        if (activeBackend == null)
            return null;

        try
        {
            string names = TextUtils.JoinParticipantNames(participants);
            StringBuilder cast = new();
            cast.AppendLine("Characters:");
            foreach (ConversationParticipantContext participant in participants)
            {
                if (participant == null || string.IsNullOrWhiteSpace(participant.displayName))
                    continue;
                AgentProfile profile = brain.GetProfile(participant.agentId);
                cast.Append("- ").Append(participant.displayName).Append(": ")
                    .Append(profile != null && !string.IsNullOrWhiteSpace(profile.conversationStyle)
                        ? profile.conversationStyle
                        : profile != null ? profile.personality : "office coworker");
                if (!string.IsNullOrWhiteSpace(participant.relationships))
                    cast.Append(" Current relationships: ").Append(participant.relationships.Trim());
                if (!string.IsNullOrWhiteSpace(participant.currentState))
                    cast.Append(" Current state: ").Append(participant.currentState.Trim());
                cast.AppendLine();
                if (profile != null)
                {
                    TextUtils.AppendRecentMemory(cast, profile, 1, participant.displayName);
                    TextUtils.AppendSocialMemory(cast, profile, 1, participant.displayName);
                }
            }
            TextUtils.AppendWorldEvents(cast, brain.WorldEvents, 1);
            cast.Append("Current world time: ").Append(brain.ScheduleContext).AppendLine();
            TextUtils.AppendRecentList(cast, "Do not repeat these recent lines:",
                brain.RecentGlobalUtterances, 3);
            TextUtils.AppendRecentList(cast, "Avoid these recent subjects:",
                brain.RecentGlobalTopics, 3);

            StringBuilder order = new();
            for (int i = 0; i < speakerOrder.Count; i++)
            {
                order.Append(i + 1).Append(". ").Append(speakerOrder[i]).AppendLine();
            }

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Write a natural spoken office conversation, including the initiating character's opening. " +
                    "Choose a specific subject from current state, memory, relationship, time, or a world event. " +
                    "Current world time is authoritative; memories describe the past and must not override its day or time of day. " +
                    "Use each profile's voice, established facts, and simple short English. " +
                    "React, question, joke, disagree, or decide instead of explaining. " +
                    "Never narrate actions, mention AI, invent shared history, or introduce absent coworkers. " +
                    "The last turn closes the exchange and is not a question. " +
                    "Extract only explicit secret, gossip, promise, favor_request, favor_done, plan, invitation, or conflict events; otherwise use an empty array. " +
                    "The openingLine is spoken by the named initiator to the other participant, is 4-18 words, and must not address the initiator by their own name. " +
                    "Return JSON only: {\"openingLine\":string,\"turns\":[{\"speaker\":string,\"line\":string}],\"socialEvents\":[{\"type\":string,\"speaker\":string,\"target\":string,\"subject\":string,\"private\":bool}]}. " +
                    "Always include all three top-level keys. Never omit turns or socialEvents. " +
                    "Return exactly " + speakerOrder.Count + " turns in the assigned order."),
                new ChatMessage("user",
                    cast + (string.IsNullOrWhiteSpace(topic)
                        ? "" : "Situation or commitment to address: " + topic + "\n") +
                    "Initiator: " + openingSpeaker + "\nNext speakers after the opening:\n" + order)
            };

            LLMOptions options = new()
            {
                requestLabel = "ConversationScript",
                temperature = Mathf.Clamp(temperature, 0.55f, 0.72f),
                maxTokens = Mathf.Clamp(38 * speakerOrder.Count + 45, 150, 230),
                jsonMode = true,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds, conversationScriptTimeoutSeconds),
                maxRetries = 1,
                retryBaseDelaySeconds = 0.75f
            };

            string raw = await activeBackend.CompleteAsync(messages, options);
            ConversationScript script = ParseConversationScript(raw, participants,
                speakerOrder, openingSpeaker, topic,
                names, out string repairSummary);
            if (!string.IsNullOrWhiteSpace(repairSummary))
                Debug.LogWarning("[Conversation script repaired] " + names + ": " + repairSummary);
            return script;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " conversation generation failed: " +
                exception.Message);
            return null;
        }
    }

    public bool IsFreshOpening(string agentId, string line)
    {
        AgentProfile profile = brain.GetProfile(agentId);
        return profile == null || !TextUtils.IsSimilarToAny(line, profile.recentOpenings, 0.72f);
    }

    public void RecordConversation(List<ConversationParticipantContext> participants,
        string topic, string openingSpeaker, string openingLine, List<ConversationTurn> turns,
        List<SocialMemoryEntry> socialEvents = null)
    {
        if (participants == null)
            return;

        foreach (ConversationParticipantContext participant in participants)
        {
            AgentProfile profile = participant != null ? brain.GetProfile(participant.agentId) : null;
            if (profile == null)
                continue;
            TextUtils.AddRecent(profile.recentTopics, topic, 8);
            if (string.Equals(participant.displayName, openingSpeaker, StringComparison.OrdinalIgnoreCase))
            {
                TextUtils.AddRecent(profile.recentOpenings, openingLine, 8);
                TextUtils.AddRecent(profile.recentUtterances, openingLine, 10);
            }
        }

        TextUtils.AddRecent(brain.RecentGlobalTopics, topic, 12);
        TextUtils.AddRecent(brain.RecentGlobalUtterances, openingLine, 24);

        if (turns != null)
        {
            foreach (ConversationTurn turn in turns)
            {
                ConversationParticipantContext participant = TextUtils.FindParticipant(participants, turn?.speaker);
                AgentProfile profile = participant != null ? brain.GetProfile(participant.agentId) : null;
                if (profile != null)
                    TextUtils.AddRecent(profile.recentUtterances, turn.line, 10);
                TextUtils.AddRecent(brain.RecentGlobalUtterances, turn.line, 24);
            }
        }

        socialEvents ??= new List<SocialMemoryEntry>();
        AddConcreteOfferEvents(participants, openingSpeaker, openingLine, turns, socialEvents);
        RecordSocialEvents(participants, socialEvents);
    }

    private static void AddConcreteOfferEvents(List<ConversationParticipantContext> participants,
        string openingSpeaker, string openingLine, List<ConversationTurn> turns,
        List<SocialMemoryEntry> socialEvents)
    {
        if (socialEvents == null)
            return;
        string previousSpeaker = "";
        AddConcreteOfferEvent(participants, openingSpeaker, openingLine, previousSpeaker, socialEvents);
        previousSpeaker = openingSpeaker;
        if (turns == null)
            return;
        foreach (ConversationTurn turn in turns)
        {
            AddConcreteOfferEvent(participants, turn?.speaker, turn?.line, previousSpeaker, socialEvents);
            if (turn != null && !string.IsNullOrWhiteSpace(turn.speaker))
                previousSpeaker = turn.speaker;
        }
    }

    private static void AddConcreteOfferEvent(List<ConversationParticipantContext> participants,
        string speaker, string line, string previousSpeaker, List<SocialMemoryEntry> socialEvents)
    {
        bool coffeeOffer = TextUtils.ContainsIgnoreCase(line, "coffee")
            && TextUtils.ContainsIgnoreCase(line, "you")
            && (TextUtils.ContainsIgnoreCase(line, "bring") || TextUtils.ContainsIgnoreCase(line, "get")
                || TextUtils.ContainsIgnoreCase(line, "fetch") || TextUtils.ContainsIgnoreCase(line, "grab"));
        if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(line) || !coffeeOffer)
            return;
        string target = previousSpeaker;
        foreach (ConversationParticipantContext participant in participants)
            if (participant != null && !string.Equals(participant.displayName, speaker,
                    StringComparison.OrdinalIgnoreCase)
                && TextUtils.ContainsIgnoreCase(line, participant.displayName))
            {
                target = participant.displayName;
                break;
            }
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, speaker,
                StringComparison.OrdinalIgnoreCase))
            return;
        socialEvents.Add(new SocialMemoryEntry
        {
            type = "promise",
            sourceAgent = speaker,
            targetAgent = target,
            subject = speaker + " promised to bring coffee to " + target + ".",
            status = "open"
        });
    }

    private void RecordSocialEvents(List<ConversationParticipantContext> participants,
        List<SocialMemoryEntry> socialEvents)
    {
        if (participants == null || socialEvents == null || socialEvents.Count == 0)
            return;

        foreach (SocialMemoryEntry socialEvent in socialEvents)
        {
            if (socialEvent == null
                || string.IsNullOrWhiteSpace(socialEvent.type)
                || string.IsNullOrWhiteSpace(socialEvent.sourceAgent)
                || string.IsNullOrWhiteSpace(socialEvent.subject))
                continue;
            int listenerCount = 0;
            foreach (ConversationParticipantContext participant in participants)
            {
                AgentProfile profile = participant != null ? brain.GetProfile(participant.agentId) : null;
                if (profile == null || HasSocialMemory(profile, socialEvent))
                    continue;

                ResolveCompletedFavor(profile, socialEvent);
                profile.socialMemory.Add(new SocialMemoryEntry
                {
                    type = socialEvent.type,
                    sourceAgent = socialEvent.sourceAgent,
                    targetAgent = socialEvent.targetAgent,
                    subject = socialEvent.subject,
                    isPrivate = socialEvent.isPrivate,
                    status = socialEvent.status
                });
                while (profile.socialMemory.Count > 20)
                    profile.socialMemory.RemoveAt(0);
                listenerCount++;
            }
            if (listenerCount > 0)
                Debug.Log("[Social memory] " + TextUtils.FormatSocialMemory(socialEvent) +
                    " (heard by " + listenerCount + ")");
        }
    }

    private static bool HasSocialMemory(AgentProfile profile, SocialMemoryEntry candidate)
    {
        int start = Mathf.Max(0, profile.socialMemory.Count - 8);
        for (int i = start; i < profile.socialMemory.Count; i++)
        {
            SocialMemoryEntry previous = profile.socialMemory[i];
            if (previous != null
                && string.Equals(previous.type, candidate.type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.sourceAgent, candidate.sourceAgent, StringComparison.OrdinalIgnoreCase)
                && TextUtils.TextSimilarity(previous.subject, candidate.subject) >= 0.75f)
                return true;
        }
        return false;
    }

    private static void ResolveCompletedFavor(AgentProfile profile, SocialMemoryEntry completed)
    {
        if (!string.Equals(completed.type, "favor_done", StringComparison.OrdinalIgnoreCase))
            return;
        for (int i = profile.socialMemory.Count - 1; i >= 0; i--)
        {
            SocialMemoryEntry previous = profile.socialMemory[i];
            if (previous == null || previous.status != "open"
                || !string.Equals(previous.type, "favor_request", StringComparison.OrdinalIgnoreCase))
                continue;
            bool samePeople = string.Equals(previous.sourceAgent, completed.targetAgent,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(previous.targetAgent, completed.sourceAgent,
                    StringComparison.OrdinalIgnoreCase);
            if (samePeople && TextUtils.TextSimilarity(previous.subject, completed.subject) >= 0.25f)
            {
                previous.status = "completed";
                return;
            }
        }
    }

    private ConversationScript ParseConversationScript(string raw,
        List<ConversationParticipantContext> participants, List<string> speakerOrder,
        string openingSpeaker, string topicSeed,
        string participantNames,
        out string repairSummary)
    {
        repairSummary = null;
        string json = TextUtils.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            repairSummary = "empty or non-JSON response; conversation omitted";
            return null;
        }

        ConversationScriptDTO script;
        try
        {
            script = JsonUtility.FromJson<ConversationScriptDTO>(json);
        }
        catch
        {
            repairSummary = "invalid JSON; conversation omitted";
            return null;
        }

        if (script == null || script.turns == null)
        {
            repairSummary = "response contained no turns; conversation omitted";
            return null;
        }

        string generatedOpening = CleanGeneratedTurn(script.openingLine,
            openingSpeaker, participantNames, out string openingRejection);
        ConversationParticipantContext openingParticipant =
            TextUtils.FindParticipant(participants, openingSpeaker);
        AgentProfile openingProfile = openingParticipant != null
            ? brain.GetProfile(openingParticipant.agentId) : null;
        string normalizedOpening = TextUtils.NormalizeForComparison(generatedOpening);
        string normalizedSpeaker = TextUtils.NormalizeForComparison(openingSpeaker);
        bool addressesSelf = normalizedOpening.StartsWith(
            normalizedSpeaker + " ", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(generatedOpening)
            || TextUtils.CountWords(generatedOpening) < 4
            || TextUtils.CountWords(generatedOpening) > 18
            || addressesSelf
            || TextUtils.IsSimilarToAny(generatedOpening,
                openingProfile?.recentOpenings, 0.78f))
        {
            repairSummary = "model opening was unusable"
                + (string.IsNullOrWhiteSpace(openingRejection)
                    ? "" : ": " + openingRejection)
                + "; conversation omitted";
            return null;
        }

        List<ConversationTurn> turns = new();
        List<string> repairs = new();
        HashSet<string> conversationLines = new()
        {
            TextUtils.NormalizeForComparison(generatedOpening)
        };
        for (int i = 0; i < speakerOrder.Count; i++)
        {
            string expectedSpeaker = speakerOrder[i];
            ConversationTurnDTO rawTurn = i < script.turns.Length ? script.turns[i] : null;
            string line = null;
            if (rawTurn == null)
            {
                repairs.Add("turn " + (i + 1) + " was missing");
            }
            else if (TextUtils.FindParticipant(participants, rawTurn.speaker) == null
                || !string.Equals(rawTurn.speaker?.Trim(), expectedSpeaker,
                    StringComparison.OrdinalIgnoreCase))
            {
                repairs.Add("turn " + (i + 1) + " had the wrong speaker");
            }
            else
            {
                line = CleanGeneratedTurn(rawTurn.line, expectedSpeaker, participantNames,
                    out string lineRejection);
                if (string.IsNullOrWhiteSpace(line))
                    repairs.Add("turn " + (i + 1) + " was unusable: " + lineRejection);
                else
                {
                    ConversationParticipantContext participant =
                        TextUtils.FindParticipant(participants, expectedSpeaker);
                    AgentProfile profile = participant != null
                        ? brain.GetProfile(participant.agentId) : null;
                    string normalized = TextUtils.NormalizeForComparison(line);
                    bool exactRepeat = conversationLines.Contains(normalized)
                        || IsExactRecentLine(normalized, profile?.recentUtterances)
                        || IsExactRecentLine(normalized, brain.RecentGlobalUtterances);
                    if (exactRepeat
                        || MentionsUnsupportedAbsentCharacter(
                            line, participants, topicSeed))
                    {
                        repairs.Add("turn " + (i + 1)
                            + (exactRepeat
                                ? " exactly repeated a recent line"
                                : " invented facts about an absent coworker"));
                        line = null;
                    }
                    else
                    {
                        conversationLines.Add(normalized);
                    }
                }
            }

            turns.Add(new ConversationTurn { speaker = expectedSpeaker, line = line });
        }

        if (script.turns.Length != speakerOrder.Count)
            repairs.Add("expected " + speakerOrder.Count + " turns but received " + script.turns.Length);
        repairSummary = repairs.Count > 0 ? string.Join("; ", repairs) : null;
        return BuildConversationScript(turns, script.socialEvents, participants,
            openingSpeaker, generatedOpening);
    }

    private bool MentionsUnsupportedAbsentCharacter(string line,
        List<ConversationParticipantContext> participants, string topicSeed)
    {
        HashSet<AgentProfile> unique = new(brain.Profiles.Values);
        foreach (AgentProfile known in unique)
        {
            string name = known?.displayName;
            if (string.IsNullOrWhiteSpace(name)
                || !TextUtils.ContainsIgnoreCase(line, name)
                || TextUtils.FindParticipant(participants, name) != null)
                continue;
            if (TextUtils.ContainsIgnoreCase(topicSeed, name)
                || ContainsNameInEstablishedContext(name, participants))
                continue;
            return true;
        }
        return false;
    }

    private bool ContainsNameInEstablishedContext(string name,
        List<ConversationParticipantContext> participants)
    {
        foreach (string worldEvent in brain.WorldEvents)
            if (TextUtils.ContainsIgnoreCase(worldEvent, name))
                return true;

        foreach (ConversationParticipantContext participant in participants)
        {
            AgentProfile profile = participant != null
                ? brain.GetProfile(participant.agentId) : null;
            if (profile == null)
                continue;
            foreach (string memory in profile.memory)
                if (TextUtils.ContainsIgnoreCase(memory, name))
                    return true;
            foreach (SocialMemoryEntry socialMemory in profile.socialMemory)
                if (socialMemory != null
                    && (TextUtils.ContainsIgnoreCase(socialMemory.subject, name)
                        || string.Equals(socialMemory.sourceAgent, name,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(socialMemory.targetAgent, name,
                            StringComparison.OrdinalIgnoreCase)))
                    return true;
        }
        return false;
    }

    private static bool IsExactRecentLine(string normalized, List<string> recentLines)
    {
        if (string.IsNullOrWhiteSpace(normalized) || recentLines == null)
            return false;
        foreach (string recent in recentLines)
            if (string.Equals(normalized, TextUtils.NormalizeForComparison(recent),
                    StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string CleanGeneratedTurn(string raw, string speakerName,
        string participantNames, out string rejection)
    {
        rejection = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            rejection = "empty line";
            return null;
        }

        string line = raw.Trim().Trim('"', '\'', '\u201c', '\u201d', '\u2018', '\u2019').Trim();
        line = TextUtils.StripSpeakerLabels(line, speakerName, participantNames);
        line = TextUtils.StripReplyLabel(line);
        string spokenLine = line;
        if (TextUtils.IsAssistantStyleReply(line)
            || TextUtils.IsNarratedReply(ref spokenLine, speakerName, participantNames))
        {
            rejection = "assistant-style or narrated line";
            return null;
        }

        return TextUtils.KeepCompleteThought(spokenLine, out rejection);
    }

    private static ConversationScript BuildConversationScript(List<ConversationTurn> turns,
        SocialEventDTO[] rawEvents, List<ConversationParticipantContext> participants,
        string openingSpeaker, string openingLine)
    {
        ConversationScript result = new();
        result.openingLine = openingLine;
        result.turns.AddRange(turns);
        if (rawEvents == null || rawEvents.Length == 0)
            return result;

        StringBuilder transcript = new();
        transcript.Append(openingSpeaker).Append(": ").AppendLine(openingLine);
        foreach (ConversationTurn turn in turns)
            if (turn != null && !string.IsNullOrWhiteSpace(turn.line))
                transcript.Append(turn.speaker).Append(": ").AppendLine(turn.line);

        int count = Mathf.Min(2, rawEvents.Length);
        for (int i = 0; i < count; i++)
        {
            SocialMemoryEntry socialEvent = ValidateSocialEvent(rawEvents[i], participants,
                transcript.ToString());
            if (socialEvent != null)
                result.socialEvents.Add(socialEvent);
        }
        return result;
    }

    private static SocialMemoryEntry ValidateSocialEvent(SocialEventDTO raw,
        List<ConversationParticipantContext> participants, string transcript)
    {
        if (raw == null || string.IsNullOrWhiteSpace(raw.type)
            || string.IsNullOrWhiteSpace(raw.speaker) || string.IsNullOrWhiteSpace(raw.subject))
            return null;

        string type = raw.type.Trim().ToLowerInvariant();
        string[] allowedTypes =
        {
            "secret", "gossip", "promise", "favor_request", "favor_done",
            "plan", "invitation", "conflict"
        };
        if (Array.IndexOf(allowedTypes, type) < 0)
            return null;

        ConversationParticipantContext source = TextUtils.FindParticipant(participants, raw.speaker);
        ConversationParticipantContext target = TextUtils.FindParticipant(participants, raw.target);
        if (source == null || (!string.IsNullOrWhiteSpace(raw.target) && target == null))
            return null;
        if (target != null && string.Equals(source.displayName, target.displayName,
                StringComparison.OrdinalIgnoreCase))
            return null;
        if (type == "favor_request"
            && !HasExplicitRequestFromSpeaker(transcript, source.displayName))
            return null;

        string subject = raw.subject.Replace('\r', ' ').Replace('\n', ' ').Trim();
        int words = TextUtils.CountWords(subject);
        if (words < 2 || words > 28 || !TextUtils.HasMeaningfulOverlap(subject, transcript))
            return null;

        bool remainsOpen = type == "promise" || type == "favor_request"
            || type == "plan" || type == "invitation";
        return new SocialMemoryEntry
        {
            type = type,
            sourceAgent = source.displayName,
            targetAgent = target != null ? target.displayName : "",
            subject = subject.TrimEnd('.', '!', '?') + ".",
            isPrivate = raw.@private,
            status = remainsOpen ? "open" : type == "favor_done" ? "completed" : "noted"
        };
    }

    private static bool HasExplicitRequestFromSpeaker(string transcript, string speaker)
    {
        if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(speaker))
            return false;

        string prefix = speaker.Trim() + ":";
        string[] requestPhrases =
        {
            "can you", "could you", "would you", "will you", "please",
            "i need you", "help me", "do me a favor"
        };
        foreach (string rawLine in transcript.Split(
                     new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string spoken = line.Substring(prefix.Length).Trim().ToLowerInvariant();
            foreach (string phrase in requestPhrases)
                if (spoken.Contains(phrase))
                    return true;
        }
        return false;
    }

    // JsonUtility assigns these fields through reflection.
#pragma warning disable CS0649
    [Serializable]
    private class ConversationScriptDTO
    {
        public string openingLine;
        public ConversationTurnDTO[] turns;
        public SocialEventDTO[] socialEvents;
    }

    [Serializable]
    private class ConversationTurnDTO
    {
        public string speaker;
        public string line;
    }

    [Serializable]
    private class SocialEventDTO
    {
        public string type;
        public string speaker;
        public string target;
        public string subject;
        public bool @private;
    }
#pragma warning restore CS0649
}
