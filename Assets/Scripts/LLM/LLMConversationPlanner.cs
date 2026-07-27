using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class LLMConversationPlanner
{
    private readonly LLMBrainService brain;
    private readonly ILLMBackend backend;

    public LLMConversationPlanner(LLMBrainService brain, ILLMBackend backend)
    {
        this.brain = brain;
        this.backend = backend;
    }

    public async Task<ConversationPlan> PlanConversationAsync(
        string agentId,
        OfficeActionType location,
        List<ConversationParticipantContext> coworkers,
        string relationships,
        string currentState,
        int memoryLines,
        float temperature,
        int requestTimeoutSeconds,
        int conversationPlanTimeoutSeconds)
    {
        AgentProfile profile = brain.GetProfile(agentId);
        if (backend == null || profile == null || coworkers == null || coworkers.Count == 0)
            return null;

        try
        {
            string who = TextUtils.DisplayName(profile, agentId);
            string candidateNames = TextUtils.JoinParticipantNames(coworkers);
            StringBuilder context = new();
            context.Append("Location: ").Append(location == OfficeActionType.BreakSpot
                ? "a shared break area" : "an office chat area").AppendLine();
            if (!string.IsNullOrWhiteSpace(currentState))
                context.Append("How you feel right now: ").Append(currentState.Trim()).AppendLine();
            if (!string.IsNullOrWhiteSpace(relationships))
                context.Append("Your relationships: ").Append(relationships.Trim()).AppendLine();
            context.AppendLine("Available coworkers:");
            foreach (ConversationParticipantContext coworker in coworkers)
            {
                if (coworker == null || string.IsNullOrWhiteSpace(coworker.displayName))
                    continue;
                AgentProfile coworkerProfile = brain.GetProfile(coworker.agentId);
                context.Append("- ").Append(coworker.displayName).Append(": ")
                    .Append(coworkerProfile != null ? coworkerProfile.personality : "coworker");
                if (!string.IsNullOrWhiteSpace(coworker.relationships))
                    context.Append(" Relationship: ").Append(coworker.relationships.Trim());
                context.AppendLine();
            }
            TextUtils.AppendRecentMemory(context, profile, Mathf.Max(2, memoryLines));
            TextUtils.AppendSocialMemory(context, profile, Mathf.Max(2, memoryLines));
            TextUtils.AppendWorldEvents(context, brain.WorldEvents, 3);
            TextUtils.AppendRecentList(context, "Subjects you recently discussed; choose something different:",
                profile.recentTopics, 6);
            TextUtils.AppendRecentList(context, "Openings you recently used; do not paraphrase them:",
                profile.recentOpenings, 6);

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Plan one believable conversation for an office-life simulation. You are " + who + ". " +
                    profile.personality + " Choose a coworker and a specific subject this person would genuinely bring up now. " +
                    "Ground it in a personal interest, an established memory, that relationship, or a recent shared event. " +
                    "Do not use vague invitations, generic check-ins, motivational language, or a recently used subject. " +
                    "Use simple everyday English. The opening must be one sentence of 4 to 12 words and give the other person something concrete to answer. " +
                    "Express only one idea. Avoid metaphors, abstract advice, corporate language, semicolons, and long explanations. " +
                    "Do not invent a past event as fact. Respond only as JSON: " +
                    "{\"targetAgent\":string,\"topic\":string,\"openingLine\":string}. " +
                    "Use each key exactly once. If the opening uses a name, it must be the target's name, never your own. " +
                    "targetAgent must be exactly one of: " + candidateNames + "."),
                new ChatMessage("user", context.ToString())
            };

            LLMOptions options = new()
            {
                requestLabel = "ConversationPlan",
                temperature = Mathf.Clamp(temperature, 0.65f, 0.85f),
                maxTokens = 120,
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ConversationPlan,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds, conversationPlanTimeoutSeconds),
                maxRetries = 1,
                retryBaseDelaySeconds = 1.5f
            };
            const int maxPlanAttempts = 2;
            for (int attempt = 0; attempt < maxPlanAttempts; attempt++)
            {
                string raw = await backend.CompleteAsync(messages, options);
                ConversationPlan plan = ParseConversationPlan(raw);
                string rejection = ValidateConversationPlan(plan, profile, coworkers, who,
                    candidateNames, out ConversationPlan validated);
                if (rejection != null)
                {
                    if (!string.IsNullOrWhiteSpace(raw))
                        Debug.LogWarning("[Conversation plan rejected] " + who + ": " + rejection);
                    if (attempt < maxPlanAttempts - 1)
                    {
                        messages.Add(new ChatMessage("user",
                            "Your previous plan was rejected: " + rejection +
                            ". Choose a different coworker, a more distinct subject, or a different opening angle."));
                        await Task.Delay(500);
                        continue;
                    }
                    return null;
                }
                return validated;
            }
            return null;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " conversation planning failed for " +
                agentId + ": " + exception.Message);
            return null;
        }
    }

    public async Task<ConversationScript> GenerateConversationAsync(
        List<ConversationParticipantContext> participants,
        string openingSpeaker,
        string openingLine,
        string topic,
        List<string> speakerOrder,
        int memoryLines,
        float temperature,
        int requestTimeoutSeconds,
        int conversationScriptTimeoutSeconds)
    {
        if (backend == null || participants == null || participants.Count < 2
            || speakerOrder == null || speakerOrder.Count == 0)
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
                    .Append(profile != null ? profile.personality : "office coworker");
                if (!string.IsNullOrWhiteSpace(participant.relationships))
                    cast.Append(" Current relationships: ").Append(participant.relationships.Trim());
                cast.AppendLine();
                if (profile != null)
                {
                    TextUtils.AppendRecentMemory(cast, profile, 1, participant.displayName);
                    TextUtils.AppendSocialMemory(cast, profile, 2, participant.displayName);
                    TextUtils.AppendRecentList(cast, participant.displayName +
                        "'s recent phrases; do not repeat them:",
                        profile.recentUtterances, 2);
                }
            }
            TextUtils.AppendWorldEvents(cast, brain.WorldEvents, 2);
            TextUtils.AppendRecentList(cast, "Recent lines heard anywhere in the office; do not reuse or lightly paraphrase:",
                brain.RecentGlobalUtterances, 8);
            TextUtils.AppendRecentList(cast, "Recent office conversation subjects; choose a different angle:",
                brain.RecentGlobalTopics, 6);

            StringBuilder order = new();
            for (int i = 0; i < speakerOrder.Count; i++)
            {
                order.Append(i + 1).Append(". ").Append(speakerOrder[i]).AppendLine();
            }

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Write the complete continuation of one natural face-to-face workplace conversation. " +
                    "Return one turn for every speaker in the assigned order. Keep the exchange broadly on the established subject, " +
                    "but allow natural reactions, questions, pronouns, thanks, jokes, hesitation, and disagreement. " +
                    "Keep the same people and established facts, and make each character sound like their profile. " +
                    "Use simple everyday spoken English. Prefer one short sentence per turn and avoid long explanations. " +
                    "Characters may naturally mention ordinary off-screen objects, food, hobbies, and places that enrich the imagined office world. " +
                    "A private social memory belongs only to the named character until they choose to say it aloud. " +
                    "Do not invent shared history, switch roles, narrate actions, or mention AI. " +
                    "The final turn must close the exchange with a statement, acknowledgment, decision, or farewell. The final turn must not end with a question. " +
                    "Also extract up to two explicit social events stated in the conversation. Allowed event types are secret, gossip, promise, favor_request, favor_done, plan, and invitation. " +
                    "Do not infer an event from ordinary chat. For each event, speaker and target must be character names, subject must be a short concrete fact or commitment, and private is true only when it was presented as confidential. " +
                    "Return an empty socialEvents array when there is no explicit event. Return only JSON in this form: " +
                    "{\"turns\":[{\"speaker\":string,\"line\":string}],\"socialEvents\":[{\"type\":string,\"speaker\":string,\"target\":string,\"subject\":string,\"private\":bool}]}." +
                    " The turns array must have exactly " + speakerOrder.Count + " entries in the assigned order."),
                new ChatMessage("user",
                    cast + "Conversation fact: " + openingSpeaker + " initiated this subject and said the opening line. " +
                    "Do not transfer " + openingSpeaker + "'s actions or memories to somebody else.\n" +
                    "Established subject: " + topic + "\n" + openingSpeaker + " said: \"" + openingLine +
                    "\"\nSpeaker order for the remaining dialogue:\n" + order)
            };

            LLMOptions options = new()
            {
                requestLabel = "ConversationScript",
                temperature = Mathf.Clamp(temperature, 0.55f, 0.72f),
                maxTokens = Mathf.Clamp(42 * speakerOrder.Count + 60, 180, 260),
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ConversationScript,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds, conversationScriptTimeoutSeconds),
                maxRetries = 0
            };

            string raw = await backend.CompleteAsync(messages, options);
            ConversationScript script = ParseConversationScript(raw, participants,
                speakerOrder, openingSpeaker, openingLine, names, out string repairSummary);
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

    public async Task<List<string>> GenerateEventMonologueAsync(
        string agentId,
        string purpose,
        string eventContext,
        int lineCount,
        int memoryLines,
        float temperature,
        int requestTimeoutSeconds,
        int conversationScriptTimeoutSeconds)
    {
        AgentProfile profile = brain.GetProfile(agentId);
        if (backend == null || profile == null)
            return null;

        lineCount = Mathf.Clamp(lineCount, 1, 4);
        string who = TextUtils.DisplayName(profile, agentId);
        StringBuilder context = new();
        if (!string.IsNullOrWhiteSpace(eventContext))
            context.AppendLine(eventContext.Trim());
        TextUtils.AppendRecentMemory(context, profile, Mathf.Max(2, memoryLines));
        TextUtils.AppendSocialMemory(context, profile, Mathf.Max(2, memoryLines));
        TextUtils.AppendWorldEvents(context, brain.WorldEvents, 3);

        try
        {
            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Write a complete short in-game speech sequence for one character. You are "
                    + who + ". " + profile.personality + " Purpose: " + purpose + ". "
                    + "Use natural everyday English that fits the character and current event. "
                    + "Each line must be one concise sentence with one clear idea. "
                    + "Do not narrate actions, mention AI, or speak for another visible office character. "
                    + "The final line must close naturally and must not be a question. "
                    + "Return only JSON in this form: "
                    + "{\"turns\":[{\"speaker\":string,\"line\":string}],\"socialEvents\":[]}. "
                    + "The turns array must contain exactly " + lineCount
                    + " entries and every speaker must be exactly \"" + who + "\"."),
                new ChatMessage("user", context.ToString())
            };
            LLMOptions options = new()
            {
                requestLabel = "EventSpeech:" + who,
                temperature = Mathf.Clamp(temperature, 0.6f, 0.78f),
                maxTokens = Mathf.Clamp(40 * lineCount + 40, 120, 180),
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ConversationScript,
                timeoutSeconds = Mathf.Min(12, Mathf.Min(requestTimeoutSeconds,
                    conversationScriptTimeoutSeconds)),
                maxRetries = 0
            };

            string raw = await backend.CompleteAsync(messages, options);
            string json = TextUtils.ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            ConversationScriptDTO script = JsonUtility.FromJson<ConversationScriptDTO>(json);
            if (script?.turns == null || script.turns.Length != lineCount)
                return null;

            List<string> lines = new();
            foreach (ConversationTurnDTO turn in script.turns)
            {
                if (turn == null || !string.Equals(turn.speaker?.Trim(), who,
                        StringComparison.OrdinalIgnoreCase))
                    return null;
                string line = TextUtils.CleanShortText(turn.line, 24);
                if (string.IsNullOrWhiteSpace(line))
                    return null;
                lines.Add(line);
            }
            if (lines.Count == 0 || IsQuestionLine(lines[lines.Count - 1]))
                return null;
            return lines;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " event speech failed for "
                + who + ": " + exception.Message);
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
            if (socialEvent == null)
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

    private static ConversationPlan ParseConversationPlan(string raw)
    {
        if (TextUtils.CountOccurrences(raw, "\"targetAgent\"") != 1
            || TextUtils.CountOccurrences(raw, "\"topic\"") != 1
            || TextUtils.CountOccurrences(raw, "\"openingLine\"") != 1)
            return null;
        string json = TextUtils.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            ConversationPlanDTO dto = JsonUtility.FromJson<ConversationPlanDTO>(json);
            if (dto == null)
                return null;
            return new ConversationPlan
            {
                targetAgent = dto.targetAgent,
                topic = dto.topic,
                openingLine = dto.openingLine
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ValidateConversationPlan(ConversationPlan plan, AgentProfile profile,
        List<ConversationParticipantContext> coworkers, string speakerName, string participantNames,
        out ConversationPlan validated)
    {
        validated = null;
        if (plan == null)
            return "response was not valid conversation-plan JSON";

        ConversationParticipantContext target = TextUtils.FindParticipant(coworkers, plan.targetAgent);
        if (target == null)
            return "targetAgent was not one of the available coworkers";

        string topic = TextUtils.CleanTopic(plan.topic);
        if (string.IsNullOrWhiteSpace(topic))
            return "topic was empty or too vague";
        if (TextUtils.IsGenericTopic(topic))
            return "topic was a generic conversation label instead of a concrete subject";
        if (TextUtils.LooksLikePersonName(topic, coworkers))
            return "topic was just a person's name instead of a concrete subject";
        if (TextUtils.IsSimilarToAny(topic, profile.recentTopics, 0.7f))
            return "topic repeated a recent subject";

        string opening = TextUtils.CleanReply(plan.openingLine, speakerName, participantNames,
            out string rejection);
        if (string.IsNullOrWhiteSpace(opening))
            return "opening line was unusable: " + rejection;
        if (opening.IndexOf(speakerName, StringComparison.OrdinalIgnoreCase) >= 0)
            return "opening addressed the initiating character instead of the coworker";
        if (TextUtils.IsGenericOpening(opening))
            return "opening was a generic check-in";
        if (TextUtils.IsSimilarToAny(opening, profile.recentOpenings, 0.72f))
            return "opening line repeated a recent opening";

        int wordCount = TextUtils.CountWords(opening);
        if (wordCount < 4 || wordCount > 24)
            return "opening line had " + wordCount + " words instead of 4-24";

        validated = new ConversationPlan
        {
            targetAgent = target.displayName,
            topic = topic,
            openingLine = opening
        };
        return null;
    }

    private ConversationScript ParseConversationScript(string raw,
        List<ConversationParticipantContext> participants, List<string> speakerOrder,
        string openingSpeaker, string openingLine, string participantNames,
        out string repairSummary)
    {
        repairSummary = null;
        string json = TextUtils.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            repairSummary = "empty or non-JSON response; using local dialogue";
            return null;
        }

        ConversationScriptDTO script;
        try
        {
            script = JsonUtility.FromJson<ConversationScriptDTO>(json);
        }
        catch
        {
            repairSummary = "invalid JSON; using local dialogue";
            return null;
        }

        if (script == null || script.turns == null)
        {
            repairSummary = "response contained no turns; using local dialogue";
            return null;
        }

        List<ConversationTurn> turns = new();
        List<string> repairs = new();
        HashSet<string> conversationLines = new()
        {
            TextUtils.NormalizeForComparison(openingLine)
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
                    if (exactRepeat)
                    {
                        repairs.Add("turn " + (i + 1) + " exactly repeated a recent line");
                        line = null;
                    }
                    else if (i == speakerOrder.Count - 1 && IsQuestionLine(line))
                    {
                        repairs.Add("final turn ended with a question");
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
            openingSpeaker, openingLine);
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

    private static bool IsQuestionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        if (line.TrimEnd().EndsWith("?", StringComparison.Ordinal))
            return true;

        string normalized = TextUtils.NormalizeForComparison(line);
        string[] questionOpeners =
        {
            "what ", "why ", "how ", "when ", "where ", "who ",
            "do ", "does ", "did ", "can ", "could ", "would ", "will ",
            "should ", "is ", "are ", "have ", "has "
        };
        foreach (string opener in questionOpeners)
            if (normalized.StartsWith(opener, StringComparison.Ordinal))
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
            "secret", "gossip", "promise", "favor_request", "favor_done", "plan", "invitation"
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

    [Serializable]
    private class ConversationPlanDTO
    {
        public string targetAgent;
        public string topic;
        public string openingLine;
    }

    [Serializable]
    private class ConversationScriptDTO
    {
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
}
