using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public sealed class OfficeEpisodePlanner
{
    private readonly LLMBrainService brain;

    public OfficeEpisodePlanner(LLMBrainService brain)
    {
        this.brain = brain;
    }

    public async Task<OfficeEpisodePack> GenerateEpisodePackAsync(
        ILLMBackend activeBackend,
        string providerLabel,
        List<AIWorkerAgent> workers,
        int requestedCount,
        float temperature,
        int timeoutSeconds)
    {
        if (activeBackend == null || activeBackend.IsLocal
            || workers == null || workers.Count < 2)
            return null;

        Dictionary<string, string> knownAgents =
            BuildAgentLookup(workers);
        if (knownAgents.Count < 2)
            return null;

        HashSet<OfficeActionType> availableActions = BuildAvailableActions();
        string availableActionNames = JoinActionNames(availableActions);
        string context = BuildCompactContext(workers);
        List<ChatMessage> messages = new()
        {
            new ChatMessage("user",
                "You are the sole story director for a persistent 2D research office. "
                + "Write a buffered pack of " + requestedCount + " distinct, executable beats. "
                + "Use only agent ids from the context. Keep personalities consistent and make "
                + "specific situations develop across beats: observations, misunderstandings, "
                + "small favors, work discoveries, humor, support, plans, and consequences. "
                + "At least 70% of the beats must be conversations. Include exactly one phone "
                + "beat and at most one activity-only beat. Put visible physical actions inside "
                + "conversation beats when possible. Conversations have 2-4 people. Use 3-4 "
                + "people at most once in this pack, only when their current states "
                + "suggest they are gathered or sharing a social area. Two-person conversations "
                + "have 3-4 lines; group conversations have 4-7 lines, every participant speaks, "
                + "and lines respond to each other. Start with the actual observation or question, "
                + "not a participant's name. No greetings, filler, exposition, "
                + "assistant language, repeated catchphrases, or numeric stat readouts. "
                + "Vary dialogue length naturally: most lines use 5-12 words and no line exceeds 18. "
                + "Match the visible mood; use anger only for genuine conflict. "
                + "A phone beat is an incoming call from someone outside the office and has one "
                + "person with 2-3 one-sided lines; never address a listed coworker. Actions with timing before "
                + "happen before dialogue; actions with timing after happen after it. If a line "
                + "claims an immediate action such as 'I'll check now', include a matching after "
                + "action for that speaker, otherwise do not say it. "
                + "Valid action types in this scene: " + availableActionNames + ". "
                + "Use VendingMachine for snacks and CoffeeMachine for coffee. "
                + "To give someone a snack or coffee, use that machine action and put the "
                + "recipient's agent id in target; the runtime fetches and delivers it. "
                + "Never invent missing, lost, or stolen snack mysteries. Snacks only enter the "
                + "world from the real vending machine. Do not mention locations or objects not "
                + "represented by the valid action types. Do not create recurring lost, missing, "
                + "or misplaced-object mysteries; prefer new work, relationship, humor, support, "
                + "or consequence-driven situations. "
                + "Every beat needs one private thought from one listed participant. It must "
                + "reveal personality, uncertainty, anticipation, or a personal reaction, not "
                + "repeat dialogue, narrate movement, issue a command, or show a numeric stat. "
                + "Action thought is also private and follows the same rules. "
                + "event is optional and must be supported by the dialogue; type is secret, "
                + "gossip, promise, favor_request, favor_done, plan, invitation, or conflict. "
                + "resolve may copy an existing scheduled thread subject when this beat fulfills it. "
                + "Return JSON only with this compact schema: "
                + "{\"beats\":[{\"id\":string,\"kind\":\"conversation|phone|activity\","
                + "\"topic\":string,\"delay\":number,\"people\":[string],"
                + "\"thought\":{\"who\":string,\"text\":string},"
                + "\"actions\":[{\"who\":string,\"type\":string,\"target\":string,"
                + "\"timing\":\"before|after\","
                + "\"seconds\":number,\"thought\":string,\"reason\":string}],"
                + "\"lines\":[{\"who\":string,\"text\":string}],\"memory\":string,"
                + "\"event\":{\"type\":string,\"from\":string,\"to\":string,"
                + "\"subject\":string,\"private\":boolean},\"resolve\":string}]}."
                + "\n\nOffice context:\n" + context)
        };

        LLMOptions options = new()
        {
            requestLabel = "EpisodePack:" + (providerLabel ?? "remote"),
            temperature = Mathf.Clamp(temperature, 0.65f, 0.82f),
            // A complete pack is cheaper than repeatedly retrying truncated JSON.
            maxTokens = Mathf.Clamp(requestedCount * 190, 1050, 1800),
            jsonMode = true,
            reasoningEffort = "low",
            excludeReasoning = true,
            timeoutSeconds = Mathf.Clamp(timeoutSeconds, 15, 45),
            maxRetries = 1,
            retryBaseDelaySeconds = 1.5f,
            highPriority = true
        };

        try
        {
            string raw = await activeBackend.CompleteAsync(messages, options);
            OfficeEpisodePack pack = ParseAndValidate(
                raw, workers, knownAgents, availableActions,
                requestedCount, providerLabel);
            if (pack == null && !string.IsNullOrWhiteSpace(raw))
                Debug.LogWarning("[Episode director] rejected an invalid pack from "
                    + providerLabel + ".");
            return pack;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Episode director] " + providerLabel
                + " generation failed: " + exception.Message);
            return null;
        }
    }

    private string BuildCompactContext(List<AIWorkerAgent> workers)
    {
        StringBuilder context = new();
        context.Append("Time: ").Append(brain.ScheduleContext).AppendLine();
        context.AppendLine("Agents:");
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || string.IsNullOrWhiteSpace(worker.AgentId))
                continue;
            AgentProfile profile = brain.GetProfile(worker.AgentId);
            context.Append("- ").Append(worker.AgentId).Append(" = ")
                .Append(worker.DisplayName).Append(" | ")
                .Append(TextUtils.CleanShortText(profile?.personality, 22))
                .Append(" | voice: ")
                .Append(TextUtils.CleanShortText(
                    profile?.conversationStyle, 10))
                .Append(" | now: ")
                .Append(TextUtils.CleanShortText(worker.GetDirectorStateSummary(), 18));
            string relationships = worker.GetEstablishedRelationships();
            if (!string.IsNullOrWhiteSpace(relationships))
                context.Append(" | ties: ")
                    .Append(TextUtils.CleanShortText(relationships, 14));
            if (profile != null && profile.memory.Count > 0)
                context.Append(" | remembers: ")
                    .Append(TextUtils.CleanShortText(
                        profile.memory[profile.memory.Count - 1], 18));
            context.AppendLine();
        }

        AppendGatheredGroups(context, workers);
        AppendRecent(context,
            "Established facts (do not repeat their discovery; show consequences)",
            brain.WorldEvents, 3);
        AppendRecent(context, "Avoid recent topics", brain.RecentGlobalTopics, 4);

        HashSet<AgentProfile> profiles = new(brain.Profiles.Values);
        int scheduled = 0;
        foreach (AgentProfile profile in profiles)
        {
            if (profile == null)
                continue;
            for (int i = profile.socialMemory.Count - 1;
                 i >= 0 && scheduled < 3; i--)
            {
                SocialMemoryEntry entry = profile.socialMemory[i];
                if (entry == null || entry.status != "scheduled")
                    continue;
                if (scheduled == 0)
                    context.AppendLine("Scheduled threads:");
                context.Append("- ").AppendLine(
                    TextUtils.CleanShortText(entry.subject, 24));
                scheduled++;
            }
            if (scheduled >= 3)
                break;
        }
        return context.ToString();
    }

    private static void AppendGatheredGroups(StringBuilder context,
        List<AIWorkerAgent> workers)
    {
        if (context == null || workers == null)
            return;
        bool wroteHeader = false;
        foreach (OfficeActionPoint point in
                 GameObject.FindObjectsOfType<OfficeActionPoint>())
        {
            if (point == null
                || !AgentConversationController.IsSocialSpot(point.actionType))
                continue;
            List<string> gathered = new();
            foreach (AIWorkerAgent worker in workers)
                if (worker != null && worker.IsActingAt(point))
                    gathered.Add(worker.AgentId);
            if (gathered.Count < 2)
                continue;
            if (!wroteHeader)
            {
                context.AppendLine("Already gathered groups:");
                wroteHeader = true;
            }
            context.Append("- ").Append(point.actionType).Append(": ")
                .AppendLine(string.Join(", ", gathered));
        }
    }

    private static void AppendRecent(StringBuilder target, string label,
        List<string> values, int count)
    {
        if (values == null || values.Count == 0)
            return;
        target.Append(label).AppendLine(":");
        int start = Mathf.Max(0, values.Count - Mathf.Max(1, count));
        for (int i = start; i < values.Count; i++)
            if (!string.IsNullOrWhiteSpace(values[i]))
                target.Append("- ").AppendLine(
                    TextUtils.CleanShortText(values[i], 20));
    }

    private OfficeEpisodePack ParseAndValidate(string raw,
        List<AIWorkerAgent> workers,
        Dictionary<string, string> knownAgents,
        HashSet<OfficeActionType> availableActions,
        int requestedCount,
        string providerLabel)
    {
        string json = TextUtils.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        EpisodePackDTO dto;
        try
        {
            dto = JsonUtility.FromJson<EpisodePackDTO>(json);
        }
        catch
        {
            return null;
        }
        if (dto?.beats == null || dto.beats.Length == 0)
            return null;

        List<OfficeEpisodeBeat> valid = new();
        List<string> packTopics = new();
        List<string> packLines = new();
        HashSet<string> beatIds = new(StringComparer.OrdinalIgnoreCase);
        int limit = Mathf.Min(10, dto.beats.Length);
        for (int i = 0; i < limit; i++)
        {
            OfficeEpisodeBeat beat = ValidateBeat(
                dto.beats[i], workers, knownAgents, availableActions,
                packTopics, packLines);
            if (beat == null)
                continue;
            if (!beatIds.Add(beat.beatId))
                beat.beatId += "-" + i;
            valid.Add(beat);
            packTopics.Add(beat.topic);
            if (beat.dialogue != null)
                foreach (OfficeEpisodeDialogueLine line in beat.dialogue)
                    if (line != null && !string.IsNullOrWhiteSpace(line.line))
                        packLines.Add(line.line);
        }

        FavorConversationMix(valid);
        int minimumCount = 2;
        int conversationCount = valid.FindAll(beat =>
            string.Equals(beat.kind, "conversation",
                StringComparison.OrdinalIgnoreCase)).Count;
        if (valid.Count < minimumCount || conversationCount < 2)
        {
            Debug.LogWarning("[Episode validation] " + providerLabel
                + " retained " + valid.Count + " of " + dto.beats.Length
                + " beats (" + conversationCount + " conversations).");
            return null;
        }

        return new OfficeEpisodePack
        {
            packId = TextUtils.CleanShortText(providerLabel, 3) + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 8),
            beats = valid.ToArray()
        };
    }

    private OfficeEpisodeBeat ValidateBeat(EpisodeBeatDTO raw,
        List<AIWorkerAgent> workers,
        Dictionary<string, string> knownAgents,
        HashSet<OfficeActionType> availableActions,
        List<string> packTopics,
        List<string> packLines)
    {
        if (raw == null || string.IsNullOrWhiteSpace(raw.kind))
            return null;
        string kind = raw.kind.Trim().ToLowerInvariant();
        if (kind != "conversation" && kind != "phone" && kind != "activity")
            return null;

        List<string> people = NormalizePeople(raw.people, knownAgents);
        List<OfficeEpisodeDialogueLine> lines =
            NormalizeLines(raw.lines, knownAgents);
        foreach (OfficeEpisodeDialogueLine line in lines)
            if (TextUtils.IsSimilarToAny(
                    line.line, packLines, 0.82f)
                || TextUtils.IsSimilarToAny(
                    line.line, brain.RecentGlobalUtterances, 0.86f))
                return null;
        if (people.Count == 0)
            AddLineSpeakers(people, lines);

        if (kind == "conversation")
        {
            if (people.Count < 2 || people.Count > 4)
                return null;
            lines.RemoveAll(line => !people.Contains(line.agentId));
            int minimumLines = people.Count == 2 ? 3 : people.Count + 1;
            int maximumLines = people.Count == 2 ? 4 : 7;
            while (lines.Count > maximumLines)
                lines.RemoveAt(lines.Count - 1);
            if (lines.Count < minimumLines
                || !ContainsAllSpeakers(lines, people)
                || TextUtils.IsGenericOpening(lines[0].line))
                return null;
            for (int i = 1; i < lines.Count; i++)
                if (string.Equals(lines[i - 1].agentId, lines[i].agentId,
                        StringComparison.OrdinalIgnoreCase))
                    return null;
        }
        else if (kind == "phone")
        {
            if (people.Count != 1 || lines.Count < 2)
                return null;
            lines.RemoveAll(line => !string.Equals(
                line.agentId, people[0], StringComparison.OrdinalIgnoreCase));
            if (lines.Count < 2 || MentionsOfficeCoworker(
                    lines, people[0], workers))
                return null;
            while (lines.Count > 3)
                lines.RemoveAt(lines.Count - 1);
        }

        List<OfficeEpisodeAction> actions =
            NormalizeActions(raw.actions, knownAgents, availableActions);
        string thoughtAgentId = ResolveAgent(raw.thought?.who, knownAgents);
        string privateThought = CleanSentence(raw.thought?.text, 18);
        if (string.IsNullOrWhiteSpace(thoughtAgentId)
            || !people.Contains(thoughtAgentId)
            || string.IsNullOrWhiteSpace(privateThought)
            || ContainsStatReadout(privateThought)
            || TextUtils.IsSimilarToAny(privateThought,
                lines.ConvertAll(line => line.line), 0.78f)
            || TextUtils.IsSimilarToAny(privateThought,
                brain.RecentGlobalUtterances, 0.84f))
            return null;
        if (kind == "conversation"
            && !EnsureExecutablePromises(
                lines, actions, people, knownAgents,
                raw.topic, availableActions))
            return null;
        if (kind == "conversation")
            EnsureDiscussedPhysicalProblemAction(
                lines, actions, people, raw.topic, availableActions);
        if (kind == "activity")
        {
            if (actions.Count == 0)
                return null;
            if (people.Count == 0)
                foreach (OfficeEpisodeAction action in actions)
                    if (!people.Contains(action.agentId))
                        people.Add(action.agentId);
        }

        string topic = TextUtils.CleanTopic(raw.topic);
        if (string.IsNullOrWhiteSpace(topic))
            topic = TextUtils.CleanTopic(raw.memory);
        if (string.IsNullOrWhiteSpace(topic) && actions.Count > 0)
            topic = TextUtils.CleanTopic(actions[0].reason);
        if (string.IsNullOrWhiteSpace(topic)
            || TextUtils.IsSimilarToAny(topic, packTopics, 0.72f)
            || TextUtils.IsSimilarToAny(topic, brain.RecentGlobalTopics, 0.78f))
            return null;
        if (IsStaleSnackMystery(raw.topic)
            || IsStaleSnackMystery(raw.memory)
            || lines.Exists(line => IsStaleSnackMystery(line?.line))
            || lines.Exists(line => MentionsUnsupportedSceneObject(line?.line))
            || MentionsUnsupportedSceneObject(raw.topic)
            || MentionsUnsupportedSceneObject(raw.memory)
            || actions.Exists(action => IsStaleSnackMystery(action?.reason)
                || MentionsUnsupportedSceneObject(action?.reason)
                || MentionsUnsupportedSceneObject(action?.thought)))
            return null;

        string beatId = TextUtils.CleanShortText(raw.id, 4);
        if (string.IsNullOrWhiteSpace(beatId))
            beatId = Guid.NewGuid().ToString("N").Substring(0, 8);

        return new OfficeEpisodeBeat
        {
            schemaVersion = 5,
            beatId = beatId,
            kind = kind,
            topic = topic,
            delaySeconds = Mathf.Clamp(
                raw.delay > 0f ? raw.delay : 30f, 24f, 38f),
            participantIds = people.ToArray(),
            thoughtAgentId = thoughtAgentId,
            privateThought = privateThought,
            actions = actions.ToArray(),
            dialogue = lines.ToArray(),
            memory = CleanSentence(raw.memory, 28),
            socialEvent = ValidateEvent(raw.@event, knownAgents, people),
            resolvesSubject = TextUtils.CleanShortText(raw.resolve, 28)
        };
    }

    private static Dictionary<string, string> BuildAgentLookup(
        List<AIWorkerAgent> workers)
    {
        Dictionary<string, string> result =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || string.IsNullOrWhiteSpace(worker.AgentId))
                continue;
            result[worker.AgentId.Trim()] = worker.AgentId.Trim();
            if (!string.IsNullOrWhiteSpace(worker.DisplayName))
                result[worker.DisplayName.Trim()] = worker.AgentId.Trim();
        }
        return result;
    }

    private static List<string> NormalizePeople(string[] raw,
        Dictionary<string, string> knownAgents)
    {
        List<string> result = new();
        if (raw == null)
            return result;
        foreach (string candidate in raw)
        {
            string id = ResolveAgent(candidate, knownAgents);
            if (!string.IsNullOrWhiteSpace(id) && !result.Contains(id))
                result.Add(id);
        }
        return result;
    }

    private static List<OfficeEpisodeDialogueLine> NormalizeLines(
        EpisodeLineDTO[] raw, Dictionary<string, string> knownAgents)
    {
        List<OfficeEpisodeDialogueLine> result = new();
        if (raw == null)
            return result;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (EpisodeLineDTO candidate in raw)
        {
            string id = ResolveAgent(candidate?.who, knownAgents);
            string line = CleanDialogueLine(candidate?.text);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(line)
                || ContainsStatReadout(line))
                continue;
            string normalized = TextUtils.NormalizeForComparison(line);
            if (!seen.Add(normalized))
                continue;
            result.Add(new OfficeEpisodeDialogueLine
            {
                agentId = id,
                line = line
            });
        }
        return result;
    }

    private static List<OfficeEpisodeAction> NormalizeActions(
        EpisodeActionDTO[] raw, Dictionary<string, string> knownAgents,
        HashSet<OfficeActionType> availableActions)
    {
        List<OfficeEpisodeAction> result = new();
        if (raw == null)
            return result;
        List<string> actionThoughts = new();
        int count = Mathf.Min(4, raw.Length);
        for (int i = 0; i < count; i++)
        {
            EpisodeActionDTO candidate = raw[i];
            string id = ResolveAgent(candidate?.who, knownAgents);
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(candidate?.type)
                || !Enum.TryParse(candidate.type.Trim(), true,
                    out OfficeActionType actionType)
                || availableActions == null
                || !availableActions.Contains(actionType)
                || actionType == OfficeActionType.PhoneCall
                || actionType == OfficeActionType.ApproachColleague
                || actionType == OfficeActionType.Custom)
                continue;
            string target = ResolveAgent(candidate.target, knownAgents);
            string thought = CleanSentence(candidate.thought, 12);
            if (ContainsStatReadout(thought)
                || TextUtils.IsSimilarToAny(
                    thought, actionThoughts, 0.8f))
                thought = "";
            else if (!string.IsNullOrWhiteSpace(thought))
                actionThoughts.Add(thought);
            result.Add(new OfficeEpisodeAction
            {
                agentId = id,
                actionType = actionType.ToString(),
                timing = string.Equals(candidate.timing, "after",
                    StringComparison.OrdinalIgnoreCase) ? "after" : "before",
                targetAgentId = target ?? "",
                destinationHint = NormalizeArea(candidate.area),
                durationSeconds = Mathf.Clamp(
                    candidate.seconds > 0f ? candidate.seconds : 4f, 2f, 15f),
                thought = thought,
                reason = TextUtils.CleanShortText(candidate.reason, 14)
            });
        }
        return result;
    }

    private static HashSet<OfficeActionType> BuildAvailableActions()
    {
        HashSet<OfficeActionType> result = new()
        {
            OfficeActionType.WalkAround,
            OfficeActionType.Think,
            OfficeActionType.CheckPhone,
            OfficeActionType.PhoneCall
        };
        foreach (OfficeActionPoint point in
                 GameObject.FindObjectsOfType<OfficeActionPoint>())
            if (point != null && point.actionType != OfficeActionType.PhoneCall
                && point.actionType != OfficeActionType.ApproachColleague
                && point.actionType != OfficeActionType.Custom)
                result.Add(point.actionType);
        return result;
    }

    private static string JoinActionNames(
        HashSet<OfficeActionType> actions)
    {
        List<string> names = new();
        if (actions != null)
            foreach (OfficeActionType action in actions)
                names.Add(action.ToString());
        names.Sort(StringComparer.Ordinal);
        return string.Join(", ", names);
    }

    private static SocialMemoryEntry ValidateEvent(EpisodeEventDTO raw,
        Dictionary<string, string> knownAgents, List<string> people)
    {
        if (raw == null || string.IsNullOrWhiteSpace(raw.type)
            || string.IsNullOrWhiteSpace(raw.subject))
            return null;
        string type = raw.type.Trim().ToLowerInvariant();
        string[] allowed =
        {
            "secret", "gossip", "promise", "favor_request", "favor_done",
            "plan", "invitation", "conflict"
        };
        if (Array.IndexOf(allowed, type) < 0)
            return null;
        string source = ResolveAgent(raw.from, knownAgents);
        string target = ResolveAgent(raw.to, knownAgents);
        if (string.IsNullOrWhiteSpace(source) || !people.Contains(source)
            || (!string.IsNullOrWhiteSpace(target) && !people.Contains(target))
            || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return null;
        bool scheduled = type == "promise" || type == "favor_request"
            || type == "plan" || type == "invitation";
        return new SocialMemoryEntry
        {
            type = type,
            sourceAgent = source,
            targetAgent = target ?? "",
            subject = CleanSentence(raw.subject, 28),
            isPrivate = raw.@private,
            status = scheduled ? "scheduled"
                : type == "favor_done" ? "completed" : "noted"
        };
    }

    private static string ResolveAgent(string raw,
        Dictionary<string, string> knownAgents)
    {
        if (string.IsNullOrWhiteSpace(raw) || knownAgents == null)
            return null;
        return knownAgents.TryGetValue(raw.Trim(), out string id) ? id : null;
    }

    private static void AddLineSpeakers(List<string> people,
        List<OfficeEpisodeDialogueLine> lines)
    {
        foreach (OfficeEpisodeDialogueLine line in lines)
            if (line != null && !people.Contains(line.agentId))
                people.Add(line.agentId);
    }

    private static bool ContainsAllSpeakers(
        List<OfficeEpisodeDialogueLine> lines, List<string> people)
    {
        if (people == null || people.Count < 2)
            return false;
        foreach (string person in people)
        {
            bool found = lines.Exists(line => string.Equals(
                line?.agentId, person, StringComparison.OrdinalIgnoreCase));
            if (!found)
                return false;
        }
        return true;
    }

    private static bool MentionsOfficeCoworker(
        List<OfficeEpisodeDialogueLine> lines, string callerId,
        List<AIWorkerAgent> workers)
    {
        foreach (AIWorkerAgent worker in workers)
        {
            if (worker == null || string.Equals(
                    worker.AgentId, callerId, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (OfficeEpisodeDialogueLine line in lines)
                if (TextUtils.ContainsIgnoreCase(line?.line, worker.DisplayName))
                    return true;
        }
        return false;
    }

    private static bool ContainsStatReadout(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        string lower = line.ToLowerInvariant();
        if (!lower.Contains("energy") && !lower.Contains("focus")
            && !lower.Contains("social") && !lower.Contains("productivity"))
            return false;
        foreach (char character in lower)
            if (char.IsDigit(character))
                return true;
        return false;
    }

    private static void FavorConversationMix(List<OfficeEpisodeBeat> beats)
    {
        if (beats == null || beats.Count == 0)
            return;

        bool keptPhone = false;
        bool keptActivity = false;
        for (int i = beats.Count - 1; i >= 0; i--)
        {
            string kind = beats[i]?.kind ?? "";
            if (string.Equals(kind, "phone", StringComparison.OrdinalIgnoreCase))
            {
                if (keptPhone)
                    beats.RemoveAt(i);
                else
                    keptPhone = true;
            }
            else if (string.Equals(kind, "activity",
                         StringComparison.OrdinalIgnoreCase))
            {
                if (keptActivity)
                    beats.RemoveAt(i);
                else
                    keptActivity = true;
            }
        }

        int conversationTotal = beats.FindAll(beat => string.Equals(
            beat?.kind, "conversation",
            StringComparison.OrdinalIgnoreCase)).Count;
        int allowedGroups = Mathf.Max(1, (conversationTotal + 3) / 4);
        int groupCount = beats.FindAll(beat => string.Equals(
                beat?.kind, "conversation",
                StringComparison.OrdinalIgnoreCase)
            && beat.participantIds != null
            && beat.participantIds.Length > 2).Count;
        for (int i = beats.Count - 1; i >= 0 && groupCount > allowedGroups; i--)
        {
            OfficeEpisodeBeat beat = beats[i];
            if (!string.Equals(beat?.kind, "conversation",
                    StringComparison.OrdinalIgnoreCase)
                || beat.participantIds == null
                || beat.participantIds.Length <= 2)
                continue;
            beats.RemoveAt(i);
            groupCount--;
        }

        int conversations = beats.FindAll(beat => string.Equals(
            beat?.kind, "conversation",
            StringComparison.OrdinalIgnoreCase)).Count;
        while (conversations * 10 < beats.Count * 7)
        {
            int removable = beats.FindLastIndex(beat => string.Equals(
                beat?.kind, "activity",
                StringComparison.OrdinalIgnoreCase));
            if (removable < 0)
                removable = beats.FindLastIndex(beat => string.Equals(
                    beat?.kind, "phone",
                    StringComparison.OrdinalIgnoreCase));
            if (removable < 0)
                break;
            beats.RemoveAt(removable);
        }

        int simpleConversation = beats.FindIndex(beat =>
            string.Equals(beat?.kind, "conversation",
                StringComparison.OrdinalIgnoreCase)
            && beat.participantIds != null
            && beat.participantIds.Length == 2);
        if (simpleConversation > 0)
        {
            OfficeEpisodeBeat firstSocialBeat = beats[simpleConversation];
            beats.RemoveAt(simpleConversation);
            beats.Insert(0, firstSocialBeat);
        }
    }

    private static bool EnsureExecutablePromises(
        List<OfficeEpisodeDialogueLine> lines,
        List<OfficeEpisodeAction> actions,
        List<string> people,
        Dictionary<string, string> knownAgents,
        string topic,
        HashSet<OfficeActionType> availableActions)
    {
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            OfficeEpisodeDialogueLine line = lines[lineIndex];
            if (!ClaimsExecutablePromise(line?.line))
                continue;

            OfficeActionType? inferred = InferPromisedAction(
                line.line, topic);
            if (!inferred.HasValue
                || !availableActions.Contains(inferred.Value))
                inferred = InferFallbackPromiseAction(availableActions);
            OfficeEpisodeAction backedAction = actions.Find(action =>
                IsMatchingAfterAction(action, line.agentId, inferred));
            string recipient = IsDeliverableAction(inferred)
                ? InferPromiseRecipient(
                    lines, lineIndex, people, knownAgents)
                : null;
            if (backedAction != null)
            {
                if (string.IsNullOrWhiteSpace(backedAction.targetAgentId)
                    && !string.IsNullOrWhiteSpace(recipient))
                    backedAction.targetAgentId = recipient;
                continue;
            }

            if (!inferred.HasValue)
                return false;
            actions.Add(new OfficeEpisodeAction
            {
                agentId = line.agentId,
                actionType = inferred.Value.ToString(),
                timing = "after",
                targetAgentId = recipient,
                durationSeconds = 5f,
                reason = TextUtils.CleanTopic(topic)
            });
        }
        return true;
    }

    private static bool IsDeliverableAction(OfficeActionType? actionType)
    {
        return actionType == OfficeActionType.VendingMachine
            || actionType == OfficeActionType.CoffeeMachine;
    }

    private static void EnsureDiscussedPhysicalProblemAction(
        List<OfficeEpisodeDialogueLine> lines,
        List<OfficeEpisodeAction> actions,
        List<string> people,
        string topic,
        HashSet<OfficeActionType> availableActions)
    {
        if (!availableActions.Contains(OfficeActionType.Printer)
            || actions.Exists(action => action != null
                && string.Equals(action.actionType,
                    OfficeActionType.Printer.ToString(),
                    StringComparison.OrdinalIgnoreCase)))
            return;

        string discussion = topic ?? "";
        foreach (OfficeEpisodeDialogueLine line in lines)
            discussion += " " + line?.line;
        string lower = discussion.ToLowerInvariant();
        if (!lower.Contains("printer")
            || !ContainsAny(lower, "jam", "stuck", "glitch", "blank",
                "error", "reset", "toner", "not working", "broken"))
            return;

        OfficeEpisodeDialogueLine actorLine = lines.Find(
            line => ClaimsExecutablePromise(line?.line));
        string actorId = actorLine?.agentId;
        if (string.IsNullOrWhiteSpace(actorId) && people.Count > 0)
            actorId = people[0];
        if (string.IsNullOrWhiteSpace(actorId))
            return;
        actions.Add(new OfficeEpisodeAction
        {
            agentId = actorId,
            actionType = OfficeActionType.Printer.ToString(),
            timing = "after",
            durationSeconds = 6f,
            reason = TextUtils.CleanTopic(topic)
        });
    }

    private static string InferPromiseRecipient(
        List<OfficeEpisodeDialogueLine> lines,
        int lineIndex,
        List<string> people,
        Dictionary<string, string> knownAgents)
    {
        OfficeEpisodeDialogueLine promise = lines[lineIndex];
        string lower = promise.line.ToLowerInvariant();
        foreach (KeyValuePair<string, string> known in knownAgents)
            if (!string.Equals(known.Value, promise.agentId,
                    StringComparison.OrdinalIgnoreCase)
                && people.Contains(known.Value)
                && lower.Contains(known.Key.ToLowerInvariant()))
                return known.Value;

        bool addressesListener = lower.Contains(" you")
            || lower.Contains("your ");
        if (!addressesListener)
            return null;
        if (lineIndex > 0
            && !string.Equals(lines[lineIndex - 1].agentId,
                promise.agentId, StringComparison.OrdinalIgnoreCase))
            return lines[lineIndex - 1].agentId;
        if (people.Count == 2)
            return people.Find(person => !string.Equals(
                person, promise.agentId,
                StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static bool IsMatchingAfterAction(
        OfficeEpisodeAction action,
        string agentId,
        OfficeActionType? inferred)
    {
        if (action == null
            || !string.Equals(action.agentId, agentId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(action.timing, "after",
                StringComparison.OrdinalIgnoreCase))
            return false;
        return !inferred.HasValue
            || Enum.TryParse(action.actionType, true,
                out OfficeActionType actionType)
            && actionType == inferred.Value;
    }

    private static bool ClaimsExecutablePromise(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        string lower = line.ToLowerInvariant();
        bool promise = lower.Contains("i'll ")
            || lower.Contains("i will ")
            || lower.Contains("let me ");
        bool deferred = lower.Contains("tomorrow")
            || lower.Contains(" later")
            || lower.Contains("next week")
            || lower.Contains("after lunch");
        return promise && !deferred;
    }

    private static OfficeActionType? InferPromisedAction(
        string line, string topic)
    {
        string lower = ((line ?? "") + " " + (topic ?? ""))
            .ToLowerInvariant();
        if (ContainsAny(lower, "printer", "paper jam", "tray", "print"))
            return OfficeActionType.Printer;
        if (ContainsAny(lower, "coffee", "espresso", "caffeine"))
            return OfficeActionType.CoffeeMachine;
        if (ContainsAny(lower, "snack", "vending"))
            return OfficeActionType.VendingMachine;
        if (ContainsAny(lower, "whiteboard", "board", "diagram"))
            return OfficeActionType.Whiteboard;
        if (ContainsAny(lower, "plant", "water it"))
            return OfficeActionType.PlantCare;
        if (ContainsAny(lower, "phone", "call"))
            return OfficeActionType.PhoneCall;
        if (ContainsAny(lower, "message", "text ", "check my phone"))
            return OfficeActionType.CheckPhone;
        if (ContainsAny(lower, "desk", "report", "document", "data",
                "code", "review", "research", "task", "email",
                "walk you through"))
            return OfficeActionType.WorkDesk;
        if (ContainsAny(lower, "break", "rest", "sit down"))
            return OfficeActionType.BreakSpot;
        if (ContainsAny(lower, "walk", "find", "search", "look around",
                "go there"))
            return OfficeActionType.WalkAround;
        if (ContainsAny(lower, "think", "consider", "figure out"))
            return OfficeActionType.Think;
        return null;
    }

    private static OfficeActionType? InferFallbackPromiseAction(
        HashSet<OfficeActionType> availableActions)
    {
        if (availableActions.Contains(OfficeActionType.WorkDesk))
            return OfficeActionType.WorkDesk;
        if (availableActions.Contains(OfficeActionType.Think))
            return OfficeActionType.Think;
        if (availableActions.Contains(OfficeActionType.WalkAround))
            return OfficeActionType.WalkAround;
        return null;
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (string value in values)
            if (text.Contains(value))
                return true;
        return false;
    }

    private static bool IsStaleSnackMystery(string text)
    {
        return LLMBrainService.IsStaleSnackMystery(text);
    }

    private static bool MentionsUnsupportedSceneObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string lower = text.ToLowerInvariant();
        string[] unsupported =
        {
            "pantry", "conference hall", "server room", "kitchen",
            "elevator", "reception desk", "storage room", "supply closet"
        };
        return Array.Exists(unsupported, lower.Contains);
    }

    private static string CleanDialogueLine(string raw)
    {
        string line = CleanSentence(raw, 18);
        if (string.IsNullOrWhiteSpace(line)
            || TextUtils.IsAssistantStyleReply(line)
            || TextUtils.CountWords(line) < 2)
            return null;
        return line;
    }

    private static string CleanSentence(string raw, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        string value = raw.Replace('\r', ' ').Replace('\n', ' ').Trim()
            .Trim('"', '\'', '\u201c', '\u201d', '\u2018', '\u2019');
        if (TextUtils.CountWords(value) > maxWords)
            value = TextUtils.CleanShortText(value, maxWords);
        if (string.IsNullOrWhiteSpace(value))
            return "";
        char last = value[value.Length - 1];
        if (last != '.' && last != '!' && last != '?')
            value += ".";
        return value;
    }

    private static string NormalizeArea(string raw)
    {
        string[] allowed =
        {
            "General", "Quiet", "Lounge", "WorkArea", "Corridor"
        };
        foreach (string value in allowed)
            if (string.Equals(raw, value, StringComparison.OrdinalIgnoreCase))
                return value;
        return "General";
    }

#pragma warning disable CS0649
    [Serializable]
    private sealed class EpisodePackDTO
    {
        public EpisodeBeatDTO[] beats;
    }

    [Serializable]
    private sealed class EpisodeBeatDTO
    {
        public string id;
        public string kind;
        public string topic;
        public float delay;
        public string[] people;
        public EpisodeThoughtDTO thought;
        public EpisodeActionDTO[] actions;
        public EpisodeLineDTO[] lines;
        public string memory;
        public EpisodeEventDTO @event;
        public string resolve;
    }

    [Serializable]
    private sealed class EpisodeThoughtDTO
    {
        public string who;
        public string text;
    }

    [Serializable]
    private sealed class EpisodeActionDTO
    {
        public string who;
        public string type;
        public string target;
        public string timing;
        public string area;
        public float seconds;
        public string thought;
        public string reason;
    }

    [Serializable]
    private sealed class EpisodeLineDTO
    {
        public string who;
        public string text;
    }

    [Serializable]
    private sealed class EpisodeEventDTO
    {
        public string type;
        public string from;
        public string to;
        public string subject;
        public bool @private;
    }
#pragma warning restore CS0649
}
