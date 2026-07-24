using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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

        if (brain.SocialRequestGate.CurrentCount == 0 || brain.PendingConversationScripts > 0)
            return null;

        await brain.SocialRequestGate.WaitAsync();
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
            string raw = await backend.CompleteAsync(messages, options);
            ConversationPlan plan = ParseConversationPlan(raw);
            string rejection = ValidateConversationPlan(plan, profile, coworkers, who,
                candidateNames, out ConversationPlan validated);
            if (rejection != null)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    Debug.LogWarning("[Conversation plan rejected] " + who + ": " + rejection);
                return null;
            }

            return validated;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " conversation planning failed for " +
                agentId + ": " + exception.Message);
            return null;
        }
        finally
        {
            brain.SocialRequestGate.Release();
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

        Interlocked.Increment(ref brain.pendingConversationScripts);
        await brain.SocialRequestGate.WaitAsync();
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
                order.Append("reply").Append(i + 1).Append(" is spoken by ")
                    .Append(speakerOrder[i]).AppendLine(".");
            }
            string replyKeys = TextUtils.BuildReplyKeyList(speakerOrder.Count);

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Continue one natural face-to-face workplace conversation. Each reply value is spoken by the assigned " +
                    "character. This short exchange has one subject only. Do not introduce a new topic, story, plan, invitation, " +
                    "or unrelated question. Each line must directly react to the immediately previous line and must mention either " +
                    "the established subject or one concrete detail from that previous line. Keep the same people and facts, and " +
                    "sound distinctively like that character. Use at least one specific profile detail, interest, relationship, " +
                    "memory, job detail, or speech-style cue across the replies; avoid reusable workplace filler. " +
                    "Use simple everyday spoken English and contractions. Let people answer, " +
                    "add a concrete detail, tease, hesitate, or disagree when appropriate; they must not merely praise or agree. " +
                    "Each line must express one clear idea in one sentence. Prefer common words a child or new English learner can understand. " +
                    "Avoid metaphors, abstract advice, corporate language, semicolons, and long explanations. " +
                    "Characters may offer to bring coffee, because they can physically fetch and deliver it. A vending machine can dispense a snack for the actor to hold locally. Do not offer snacks, food, gifts, or other unavailable objects unless the action is VendingMachine. " +
                    "Do not claim someone brought, carries, owns, gives, or received an object unless the opening line explicitly establishes that physical object. " +
                    "A private social memory belongs only to the named character until they choose to say it aloud. " +
                    "Every reply value must be non-empty and use 3 to 12 words. Do not invent shared history, switch roles, narrate actions, use speaker labels " +
                    "inside a line, mention AI, or repeat wording. Do not end every line with a question. " +
                    "If the opening already congratulated someone, do not repeat the exact phrase happy birthday; add a personal wish, thanks, joke, or gift reaction instead. " +
                    "Also extract up to two explicit social events stated in the conversation. Allowed event types are secret, gossip, promise, favor_request, favor_done, plan, and invitation. " +
                    "Do not infer an event from ordinary chat. For each event, speaker and target must be character names, subject must be a short concrete fact or commitment, and private is true only when it was presented as confidential. " +
                    "Return an empty socialEvents array when there is no explicit event. Return only JSON with these reply keys: " + replyKeys +
                    ", plus socialEvents:[{type:string,speaker:string,target:string,subject:string,private:bool}]."),
                new ChatMessage("user",
                    cast + "Conversation fact: " + openingSpeaker + " initiated this subject and said the opening line. " +
                    "Do not transfer " + openingSpeaker + "'s actions or memories to somebody else.\n" +
                    "Established subject: " + topic + "\n" + openingSpeaker + " said: \"" + openingLine +
                    "\"\nReply assignments:\n" + order)
            };

            LLMOptions options = new()
            {
                requestLabel = "ConversationScript",
                temperature = Mathf.Clamp(temperature, 0.55f, 0.72f),
                maxTokens = Mathf.Clamp(56 * speakerOrder.Count + 100, 220, 440),
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ConversationScript,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds, conversationScriptTimeoutSeconds)
            };

            const int maxAttempts = 3;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                string raw = await backend.CompleteAsync(messages, options);
                ConversationScript script = ParseAndValidateConversationScript(raw, participants,
                    speakerOrder, openingSpeaker, openingLine, topic, names, out string rejection);

                if (script != null && string.IsNullOrWhiteSpace(rejection))
                    return script;

                if (!string.IsNullOrWhiteSpace(rejection))
                    Debug.LogWarning("[Conversation script " + (script == null ? "rejected" : "partial")
                        + " attempt " + (attempt + 1) + "/" + maxAttempts + "] " + names + ": " + rejection);

                if (script != null)
                    return script;

                if (attempt < maxAttempts - 1)
                {
                    if (!string.IsNullOrWhiteSpace(rejection))
                        messages.Add(new ChatMessage("user",
                            "Your previous response was rejected: " + rejection +
                            ". Please regenerate all reply values keeping every line strictly on the established subject. " +
                            "Each line must share at least one concrete keyword with the topic or the previous line."));
                    await System.Threading.Tasks.Task.Delay(500);
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " conversation generation failed: " +
                exception.Message);
            return null;
        }
        finally
        {
            brain.SocialRequestGate.Release();
            Interlocked.Decrement(ref brain.pendingConversationScripts);
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

    private ConversationScript ParseAndValidateConversationScript(string raw,
        List<ConversationParticipantContext> participants, List<string> speakerOrder,
        string openingSpeaker, string openingLine, string topic, string participantNames,
        out string rejection)
    {
        rejection = null;
        string json = TextUtils.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            rejection = "empty or non-JSON response";
            return null;
        }

        ConversationScriptDTO script;
        try
        {
            script = JsonUtility.FromJson<ConversationScriptDTO>(json);
        }
        catch
        {
            rejection = "invalid JSON";
            return null;
        }

        if (script == null)
        {
            rejection = "script contained no turns";
            return null;
        }

        List<ConversationTurn> accepted = new();
        ConversationThreadState thread = new(topic, openingLine);
        List<string> lines = new() { openingLine };
        string[] generatedLines =
        {
            script.reply1,
            script.reply2,
            script.reply3,
            script.reply4,
            script.reply5,
            script.reply6
        };
        int count = Mathf.Min(generatedLines.Length, speakerOrder.Count);
        for (int i = 0; i < count; i++)
        {
            string expectedSpeaker = speakerOrder[i];
            string line = TextUtils.CleanReply(generatedLines[i], expectedSpeaker, participantNames,
                out string lineRejection);
            ConversationParticipantContext participant = TextUtils.FindParticipant(participants, expectedSpeaker);
            AgentProfile profile = participant != null ? brain.GetProfile(participant.agentId) : null;
            if (string.IsNullOrWhiteSpace(line))
            {
                rejection = "turn " + (i + 1) + " was unusable: " + lineRejection;
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }
            if (TextUtils.IsSimilarToAny(line, lines, 0.8f)
                || (profile != null && TextUtils.IsSimilarToAny(line, profile.recentUtterances, 0.78f)))
            {
                rejection = "turn " + (i + 1) + " repeated a recent line";
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }
            if (TextUtils.IsSimilarToAny(line, brain.RecentGlobalUtterances, 0.82f))
            {
                rejection = "turn " + (i + 1) + " repeated a line recently heard elsewhere";
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }
            if (!thread.TryAccept(line))
            {
                rejection = "turn " + (i + 1) + " changed to an unrelated subject";
                return accepted.Count > 0
                    ? BuildConversationScript(accepted, null, participants,
                        openingSpeaker, openingLine)
                    : null;
            }

            accepted.Add(new ConversationTurn { speaker = expectedSpeaker, line = line });
            lines.Add(line);
        }

        if (accepted.Count == 0)
        {
            rejection = "script had no valid turns";
            return null;
        }
        return BuildConversationScript(accepted, script.socialEvents, participants,
            openingSpeaker, openingLine);
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
        transcript.Append(openingSpeaker).Append(": ").Append(openingLine).Append(' ');
        foreach (ConversationTurn turn in turns)
            transcript.Append(turn.speaker).Append(": ").Append(turn.line).Append(' ');

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

    private sealed class ConversationThreadState
    {
        private readonly HashSet<string> subjectAnchors;
        private readonly HashSet<string> topicKeywords;
        private HashSet<string> previousAnchors;

        public ConversationThreadState(string topic, string openingLine)
        {
            subjectAnchors = TextUtils.MeaningfulWords(TextUtils.NormalizeForComparison(
                (topic ?? "") + " " + (openingLine ?? "")));
            topicKeywords = TextUtils.MeaningfulWords(TextUtils.NormalizeForComparison(topic));
            previousAnchors = TextUtils.MeaningfulWords(TextUtils.NormalizeForComparison(openingLine));
        }

        public bool TryAccept(string line)
        {
            string normalized = TextUtils.NormalizeForComparison(line);
            if (StartsNewSubject(normalized))
                return false;

            HashSet<string> lineAnchors = TextUtils.MeaningfulWords(normalized);
            bool anchored = lineAnchors.Overlaps(subjectAnchors)
                || lineAnchors.Overlaps(previousAnchors)
                || lineAnchors.Overlaps(topicKeywords)
                || IsDirectReaction(normalized)
                || IsOnTopicQuestion(normalized);
            if (!anchored)
                return false;

            previousAnchors = lineAnchors;
            return true;
        }

        private static bool StartsNewSubject(string line)
        {
            string[] starters =
            {
                "by the way", "speaking of something else", "on another topic",
                "anyway have you", "forget that"
            };
            foreach (string starter in starters)
                if (line.StartsWith(starter, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool IsDirectReaction(string line)
        {
            string[] reactions =
            {
                "that sounds", "tell me more", "why do you", "i agree", "i disagree",
                "you are right", "good idea", "bad idea", "what happened next",
                "how do you", "what about", "have you tried", "do you think",
                "would you", "could you", "should we", "what if",
                "yes", "no", "exactly", "maybe", "really", "thank you", "thanks",
                "oh", "wow", "hmm", "wait", "so", "well", "right"
            };
            foreach (string reaction in reactions)
                if (line.StartsWith(reaction, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool IsOnTopicQuestion(string line)
        {
            if (!line.EndsWith("?", StringComparison.Ordinal))
                return false;
            int wordCount = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return wordCount >= 3 && wordCount <= 14;
        }
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
        public string reply1;
        public string reply2;
        public string reply3;
        public string reply4;
        public string reply5;
        public string reply6;
        public SocialEventDTO[] socialEvents;
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
