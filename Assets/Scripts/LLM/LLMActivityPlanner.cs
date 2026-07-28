using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class LLMActivityPlanner
{
    private readonly LLMBrainService brain;

    public LLMActivityPlanner(LLMBrainService brain)
    {
        this.brain = brain;
    }

    public async Task<List<OfficeActivityPlan>> PlanActivityBatchAsync(
        string agentId,
        List<OfficeActionType> availableActions,
        List<ConversationParticipantContext> coworkers,
        string currentState,
        int requestedCount,
        int memoryLines,
        float temperature,
        int requestTimeoutSeconds,
        int activityPlanTimeoutSeconds)
    {
        AgentProfile profile = brain.GetProfile(agentId);
        if (profile == null || availableActions == null || availableActions.Count == 0)
            return null;

        requestedCount = Mathf.Clamp(requestedCount, 2, 6);
        List<OfficeActivityPlan> commitmentPlans = BuildOpenCommitmentPlans(
            profile, availableActions, coworkers);
        ILLMBackend activeBackend = brain.GetBackendForAgent(agentId);

        if (activeBackend == null)
            return commitmentPlans;

        try
        {
            string who = TextUtils.DisplayName(profile, agentId);
            string allowed = TextUtils.JoinActionTypes(availableActions);
            string coworkerNames = TextUtils.JoinParticipantNames(coworkers);
            StringBuilder context = new();
            if (!string.IsNullOrWhiteSpace(currentState))
                context.Append("Current state: ").Append(currentState.Trim()).AppendLine();
            if (coworkers != null)
            {
                foreach (ConversationParticipantContext coworker in coworkers)
                {
                    if (coworker == null || string.IsNullOrWhiteSpace(coworker.displayName))
                        continue;
                    context.Append("Coworker ").Append(coworker.displayName).Append(": ")
                        .Append(string.IsNullOrWhiteSpace(coworker.currentState)
                            ? "state unknown" : coworker.currentState.Trim()).AppendLine();
                }
            }
            TextUtils.AppendRecentMemory(context, profile, 1);
            TextUtils.AppendSocialMemory(context, profile, 1);
            TextUtils.AppendWorldEvents(context, brain.WorldEvents, 1);

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Plan " + requestedCount + " varied physical office activities for " +
                    who + ". Profile: " + profile.personality + " Fit current needs and memory. " +
                    "Do not repeat actions or overuse desk work. Honor one open social commitment when practical. " +
                    "Independent activities use empty sequenceId/objective and sequenceStep 0. A 2-4 step objective uses one sequenceId, one objective, and increasing sequenceStep values. " +
                    "ActionPoint targets a scene object; FreePosition a reachable area; CurrentPosition no travel; FollowAgent only ApproachColleague. PhoneCall always uses CurrentPosition and can happen anywhere. " +
                    "If a phone call is due, include one PhoneCall unless a commitment fills the batch. " +
                    "Custom is a short object-free action using FreePosition or CurrentPosition; it cannot create, carry, or transfer items. Food must use VendingMachine. " +
                    "Use coworkers' visible state: someone tired, lonely, or sad may inspire a supportive visit, snack gift, or conversation, but do not invent facts. " +
                    "For a shared snack discussion, make a sequence VendingMachine then BreakSpot/ChatSpot with a named target; set companionPreparation to Snack on the social step if both should get snacks. " +
                    "For a gift, make a sequence VendingMachine then ApproachColleague and set giveHeldItemToTarget true only on the approach step. " +
                    "To fulfill a commitment, copy its exact subject into socialMemorySubject. Social actions use BreakSpot or ChatSpot and a named target. " +
                    "Respond only as JSON: " +
                    "{\"activities\":[{\"actionType\":string,\"destinationMode\":string,\"destinationHint\":string," +
                    "\"sequenceId\":string,\"sequenceStep\":number,\"objective\":string," +
                    "\"targetAgent\":string,\"durationSeconds\":number,\"reason\":string,\"thought\":string," +
                    "\"customActionLabel\":string,\"energyChange\":number,\"focusChange\":number,\"socialChange\":number,\"productivityChange\":number,\"socialMemorySubject\":string,\"companionPreparation\":string,\"giveHeldItemToTarget\":boolean}" +
                    "]}. Return exactly " + requestedCount + " activity objects. " +
                    "actionType must be one of: " + allowed + ", or Custom. " +
                    "destinationMode is ActionPoint, FreePosition, CurrentPosition, or FollowAgent. Free destinationHint is General, Quiet, Lounge, WorkArea, or Corridor. " +
                    (string.IsNullOrWhiteSpace(coworkerNames)
                        ? "No coworkers are currently available, so do not choose ApproachColleague. "
                        : "For ApproachColleague, targetAgent must be exactly one of: " + coworkerNames + ". ") +
                    "ApproachColleague thought is the 4-12 word arrival line. durationSeconds is 2-30; reason is 3-14 words; other thoughts are at most 10 words."),
                new ChatMessage("user", context.ToString())
            };

            LLMOptions options = new()
            {
                requestLabel = "ActivityBatch:" + who,
                temperature = Mathf.Clamp(temperature, 0.55f, 0.8f),
                maxTokens = Mathf.Clamp(requestedCount * 90, 220, 360),
                jsonMode = true,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds,
                    Mathf.Max(8, activityPlanTimeoutSeconds)),
                maxRetries = 0
            };
            string raw = await activeBackend.CompleteAsync(messages, options);
            List<OfficeActivityPlan> plans = ParseActivityPlans(
                raw, availableActions, coworkers, requestedCount, profile);
            plans = PrependCommitment(plans, commitmentPlans, requestedCount);
            if ((plans == null || plans.Count == 0) && !string.IsNullOrWhiteSpace(raw))
                Debug.LogWarning("[Activity batch rejected] " + who +
                    ": no valid available activities");
            return plans;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " activity planning failed for " +
                agentId + ": " + exception.Message);
            return commitmentPlans;
        }
    }

    private static List<OfficeActivityPlan> PrependCommitment(List<OfficeActivityPlan> plans,
        List<OfficeActivityPlan> commitments, int capacity)
    {
        if (commitments == null || commitments.Count == 0)
            return plans;
        plans ??= new List<OfficeActivityPlan>();
        for (int i = 0; i < plans.Count; i++)
        {
            if (plans[i] == null || string.IsNullOrWhiteSpace(plans[i].socialMemorySubject))
                continue;

            string seqId = plans[i].sequenceId;
            int takeStart = i;
            int takeEnd = i + 1;
            if (!string.IsNullOrWhiteSpace(seqId))
            {
                while (takeStart > 0 && string.Equals(plans[takeStart - 1]?.sequenceId, seqId,
                    StringComparison.OrdinalIgnoreCase))
                    takeStart--;
                while (takeEnd < plans.Count && string.Equals(plans[takeEnd]?.sequenceId, seqId,
                    StringComparison.OrdinalIgnoreCase))
                    takeEnd++;
            }

            List<OfficeActivityPlan> taken = new();
            for (int j = takeStart; j < takeEnd; j++)
                taken.Add(plans[j]);
            for (int j = takeEnd - 1; j >= takeStart; j--)
                plans.RemoveAt(j);
            for (int j = taken.Count - 1; j >= 0; j--)
                plans.Insert(0, taken[j]);

            while (plans.Count > Mathf.Max(1, capacity))
                plans.RemoveAt(plans.Count - 1);
            return plans;
        }
        Debug.Log("[Commitment plan] fallback queued " + commitments.Count +
            " activity(ies) for commitment: " + commitments[0].socialMemorySubject);
        int insertIndex = 0;
        foreach (OfficeActivityPlan plan in commitments)
        {
            plans.Insert(insertIndex, plan);
            insertIndex++;
        }
        while (plans.Count > Mathf.Max(1, capacity))
            plans.RemoveAt(plans.Count - 1);
        return plans;
    }

    private static List<OfficeActivityPlan> BuildOpenCommitmentPlans(AgentProfile profile,
        List<OfficeActionType> availableActions, List<ConversationParticipantContext> coworkers)
    {
        string actor = TextUtils.DisplayName(profile, profile.agentId);
        for (int i = profile.socialMemory.Count - 1; i >= 0; i--)
        {
            SocialMemoryEntry entry = profile.socialMemory[i];
            if (entry == null || entry.status != "open")
                continue;

            bool actorResponsible = string.Equals(entry.sourceAgent, actor,
                StringComparison.OrdinalIgnoreCase);
            if (entry.type == "favor_request" && !string.IsNullOrWhiteSpace(entry.targetAgent))
                actorResponsible = string.Equals(entry.targetAgent, actor,
                    StringComparison.OrdinalIgnoreCase);
            if (!actorResponsible)
                continue;

            if (entry.type == "invitation")
            {
                ConversationParticipantContext target = TextUtils.FindParticipant(coworkers, entry.targetAgent);
                if (target == null)
                    continue;
                OfficeActionType? socialType = availableActions.Contains(OfficeActionType.BreakSpot)
                    ? OfficeActionType.BreakSpot
                    : availableActions.Contains(OfficeActionType.ChatSpot)
                        ? OfficeActionType.ChatSpot : null;
                if (!socialType.HasValue)
                    continue;
                Debug.Log("[Social commitment] " + actor + " fulfilling invitation: " +
                    entry.subject);
                return new List<OfficeActivityPlan>
                {
                    new OfficeActivityPlan
                    {
                        actionType = socialType.Value,
                        destinationMode = OfficeDestinationMode.ActionPoint,
                        targetAgent = target.displayName,
                        durationSeconds = 8f,
                        reason = "follow through on the invitation",
                        thought = "I should keep that invitation.",
                        socialMemorySubject = entry.subject,
                        completesSocialCommitment = true
                    }
                };
            }

            if (entry.type == "promise" || entry.type == "favor_request" || entry.type == "plan")
            {
                if (IsBirthdayPreparation(entry.subject))
                {
                    Debug.Log("[Social commitment] " + actor
                        + " preparing birthday surprise: " + entry.subject);
                    return new List<OfficeActivityPlan>
                    {
                        new OfficeActivityPlan
                        {
                            actionType = OfficeActionType.Custom,
                            destinationMode = OfficeDestinationMode.FreePosition,
                            destinationHint = "Lounge",
                            durationSeconds = 6f,
                            reason = "prepare the birthday surprise",
                            thought = "I hope they enjoy what we prepared.",
                            customActionLabel = "prepare birthday surprise",
                            focusChange = 1f,
                            socialChange = 2f,
                            socialMemorySubject = entry.subject,
                            completesSocialCommitment = true
                        }
                    };
                }

                bool snackDelivery = TextUtils.ContainsIgnoreCase(entry.subject, "snack");
                if (snackDelivery)
                {
                    ConversationParticipantContext target =
                        TextUtils.FindParticipant(coworkers, entry.targetAgent);
                    if (target == null
                        || !availableActions.Contains(OfficeActionType.ApproachColleague))
                        continue;

                    AIWorkerAgent actorAgent = FindLiveAgent(actor);
                    if (actorAgent != null && actorAgent.HeldItemKind == SceneItemKind.Snack)
                    {
                        Debug.Log("[Social commitment] " + actor + " delivering snack: " +
                            entry.subject);
                        return new List<OfficeActivityPlan>
                        {
                            BuildItemDeliveryApproach(entry, target, "snack")
                        };
                    }
                    if (!availableActions.Contains(OfficeActionType.VendingMachine))
                        continue;

                    string sequenceId = "snack-delivery";
                    string objective = "bring a snack to " + target.displayName;
                    Debug.Log("[Social commitment] " + actor + " getting a snack for: " +
                        entry.subject);
                    return new List<OfficeActivityPlan>
                    {
                        new OfficeActivityPlan
                        {
                            actionType = OfficeActionType.VendingMachine,
                            destinationMode = OfficeDestinationMode.ActionPoint,
                            sequenceId = sequenceId,
                            sequenceStep = 1,
                            objective = objective,
                            durationSeconds = 3f,
                            reason = "get the promised snack",
                            thought = "I promised to bring a snack.",
                            socialMemorySubject = entry.subject,
                            completesSocialCommitment = false
                        },
                        new OfficeActivityPlan
                        {
                            actionType = OfficeActionType.ApproachColleague,
                            destinationMode = OfficeDestinationMode.FollowAgent,
                            targetAgent = target.displayName,
                            sequenceId = sequenceId,
                            sequenceStep = 2,
                            objective = objective,
                            durationSeconds = 2f,
                            reason = "deliver the promised snack",
                            thought = "I should deliver this snack.",
                            socialMemorySubject = entry.subject,
                            completesSocialCommitment = true
                        }
                    };
                }

                bool coffeeDelivery = TextUtils.ContainsIgnoreCase(entry.subject, "coffee")
                    || TextUtils.ContainsIgnoreCase(entry.subject, "drink");
                if (coffeeDelivery)
                {
                    ConversationParticipantContext target = TextUtils.FindParticipant(coworkers, entry.targetAgent);
                    if (target == null)
                        continue;
                    AIWorkerAgent actorAgent = FindLiveAgent(actor);
                    if (actorAgent != null && actorAgent.HeldItemKind == SceneItemKind.Coffee)
                    {
                        Debug.Log("[Social commitment] " + actor + " delivering: " +
                            entry.subject);
                        return new List<OfficeActivityPlan>
                        {
                            new OfficeActivityPlan
                            {
                                actionType = OfficeActionType.ApproachColleague,
                                destinationMode = OfficeDestinationMode.FollowAgent,
                                targetAgent = target.displayName,
                                durationSeconds = 2f,
                                reason = "deliver the promised coffee",
                                thought = "I should deliver this coffee.",
                                socialMemorySubject = entry.subject,
                                completesSocialCommitment = true
                            }
                        };
                    }
                    if (!availableActions.Contains(OfficeActionType.CoffeeMachine))
                        continue;
                    Debug.Log("[Social commitment] " + actor + " getting coffee for: " +
                        entry.subject);
                    return new List<OfficeActivityPlan>
                    {
                        new OfficeActivityPlan
                        {
                            actionType = OfficeActionType.CoffeeMachine,
                            destinationMode = OfficeDestinationMode.ActionPoint,
                            durationSeconds = 3f,
                            reason = "get the promised coffee",
                            thought = "I promised to bring coffee.",
                            socialMemorySubject = entry.subject,
                            completesSocialCommitment = false
                        }
                    };
                }
                if (TextUtils.ContainsUnavailableObjectClaim(entry.subject))
                    continue;
                string counterpartName = string.Equals(entry.sourceAgent, actor,
                        StringComparison.OrdinalIgnoreCase)
                    ? entry.targetAgent : entry.sourceAgent;
                ConversationParticipantContext counterpart = TextUtils.FindParticipant(coworkers, counterpartName);
                if (counterpart == null || !availableActions.Contains(OfficeActionType.ApproachColleague))
                    continue;
                Debug.Log("[Social commitment] " + actor + " approaching " +
                    counterpart.displayName + " about: " + entry.subject);
                return new List<OfficeActivityPlan>
                {
                    new OfficeActivityPlan
                    {
                        actionType = OfficeActionType.ApproachColleague,
                        destinationMode = OfficeDestinationMode.FollowAgent,
                        targetAgent = counterpart.displayName,
                        durationSeconds = 5f,
                        reason = entry.subject,
                        thought = counterpart.displayName + ", can we handle what we discussed?",
                        socialChange = 2f,
                        socialMemorySubject = entry.subject,
                        completesSocialCommitment = true
                    }
                };
            }
        }
        return null;
    }

    private static OfficeActivityPlan BuildItemDeliveryApproach(
        SocialMemoryEntry entry, ConversationParticipantContext target, string itemName)
    {
        return new OfficeActivityPlan
        {
            actionType = OfficeActionType.ApproachColleague,
            destinationMode = OfficeDestinationMode.FollowAgent,
            targetAgent = target.displayName,
            durationSeconds = 2f,
            reason = "deliver the promised " + itemName,
            thought = "I should deliver this " + itemName + ".",
            socialMemorySubject = entry.subject,
            completesSocialCommitment = true
        };
    }

    private static AIWorkerAgent FindLiveAgent(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (AIWorkerAgent agent in GameObject.FindObjectsOfType<AIWorkerAgent>())
            if (agent != null && string.Equals(agent.DisplayName, displayName,
                    StringComparison.OrdinalIgnoreCase))
                return agent;
        return null;
    }

    private List<OfficeActivityPlan> ParseActivityPlans(
        string raw,
        List<OfficeActionType> availableActions,
        List<ConversationParticipantContext> coworkers,
        int maxPlans,
        AgentProfile profile)
    {
        if (TextUtils.CountOccurrences(raw, "\"activities\"") != 1)
            return null;
        string json = TextUtils.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            ActivityPlanBatchDTO batch = JsonUtility.FromJson<ActivityPlanBatchDTO>(json);
            if (batch == null || batch.activities == null || batch.activities.Length == 0)
                return null;

            List<OfficeActivityPlan> result = new();
            OfficeActionType? previousType = null;
            int count = Mathf.Min(Mathf.Max(1, maxPlans), batch.activities.Length);
            for (int i = 0; i < count; i++)
            {
                OfficeActivityPlan plan = ParseActivityPlanItem(
                    batch.activities[i], availableActions, coworkers, profile);
                if (plan == null || previousType == plan.actionType)
                    continue;

                result.Add(plan);
                previousType = plan.actionType;
            }
            NormalizeActivitySequences(result);
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private OfficeActivityPlan ParseActivityPlanItem(
        ActivityPlanDTO dto,
        List<OfficeActionType> availableActions,
        List<ConversationParticipantContext> coworkers,
        AgentProfile profile)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.actionType))
            return null;

        OfficeActionType actionType;
        bool isCustom = string.Equals(dto.actionType.Trim(), "Custom", StringComparison.OrdinalIgnoreCase);
        if (!isCustom)
        {
            if (!Enum.TryParse(dto.actionType.Trim(), true, out actionType)
                || availableActions == null || !availableActions.Contains(actionType))
                return null;
        }
        else
        {
            actionType = OfficeActionType.Custom;
            string customDescription = (dto.customActionLabel ?? "") + " "
                + (dto.reason ?? "") + " " + (dto.thought ?? "");
            if (TextUtils.ClaimsUnavailableObjectHandling(customDescription))
            {
                Debug.LogWarning("[Activity rejected] " + TextUtils.DisplayName(profile, profile.agentId)
                    + ": unavailable object action: " + customDescription.Trim());
                return null;
            }
        }

        OfficeDestinationMode destinationMode;
        if (isCustom)
        {
            destinationMode = !string.IsNullOrWhiteSpace(dto.destinationMode)
                && Enum.TryParse(dto.destinationMode.Trim(), true, out OfficeDestinationMode parsedMode)
                ? parsedMode
                : OfficeDestinationMode.FreePosition;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(dto.destinationMode)
                || !Enum.TryParse(dto.destinationMode.Trim(), true, out destinationMode))
                return null;
        }

        string targetAgent = TextUtils.CleanShortText(dto.targetAgent, 6);
        bool requiresCoworker = destinationMode == OfficeDestinationMode.FollowAgent
            || actionType == OfficeActionType.ApproachColleague;
        bool socialAction = actionType == OfficeActionType.BreakSpot
            || actionType == OfficeActionType.ChatSpot;
        if ((requiresCoworker || (socialAction && !string.IsNullOrWhiteSpace(targetAgent)))
            && TextUtils.FindParticipant(coworkers, targetAgent) == null)
            return null;

        SocialMemoryEntry commitment = FindOpenSocialMemory(profile, dto.socialMemorySubject);
        if (commitment != null && commitment.type == "invitation"
            && (!socialAction || destinationMode != OfficeDestinationMode.ActionPoint
                || !string.Equals(targetAgent, commitment.targetAgent,
                    StringComparison.OrdinalIgnoreCase)))
            commitment = null;
        if (commitment != null && commitment.type != "invitation")
        {
            string actor = TextUtils.DisplayName(profile, profile.agentId);
            string counterpartName = string.Equals(commitment.sourceAgent, actor,
                    StringComparison.OrdinalIgnoreCase)
                ? commitment.targetAgent : commitment.sourceAgent;
            bool validInteraction = !TextUtils.ContainsUnavailableObjectClaim(commitment.subject)
                && actionType == OfficeActionType.ApproachColleague
                && destinationMode == OfficeDestinationMode.FollowAgent
                && string.Equals(targetAgent, counterpartName,
                    StringComparison.OrdinalIgnoreCase);
            bool validBirthdayPreparation = IsBirthdayPreparation(commitment.subject)
                && actionType == OfficeActionType.Custom
                && (destinationMode == OfficeDestinationMode.FreePosition
                    || destinationMode == OfficeDestinationMode.CurrentPosition);
            if (!validInteraction && !validBirthdayPreparation)
            {
                Debug.LogWarning("[Commitment action rejected] "
                    + TextUtils.DisplayName(profile, profile.agentId) + ": action did not physically fulfill: "
                    + commitment.subject);
                commitment = null;
            }
        }

        string companionPreparation = TextUtils.CleanShortText(
            dto.companionPreparation, 1);
        if (!socialAction || string.IsNullOrWhiteSpace(targetAgent)
            || (!string.Equals(companionPreparation, "Snack",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(companionPreparation, "Coffee",
                    StringComparison.OrdinalIgnoreCase)))
            companionPreparation = "";

        bool giveHeldItemToTarget = dto.giveHeldItemToTarget
            && actionType == OfficeActionType.ApproachColleague
            && destinationMode == OfficeDestinationMode.FollowAgent
            && !string.IsNullOrWhiteSpace(targetAgent)
            && !string.IsNullOrWhiteSpace(dto.sequenceId);

        return new OfficeActivityPlan
        {
            actionType = actionType,
            destinationMode = destinationMode,
            destinationHint = TextUtils.CleanShortText(dto.destinationHint, 3),
            sequenceId = TextUtils.CleanShortText(dto.sequenceId, 4),
            sequenceStep = Mathf.Max(0, dto.sequenceStep),
            objective = TextUtils.CleanShortText(dto.objective, 12),
            targetAgent = targetAgent,
            durationSeconds = Mathf.Clamp(dto.durationSeconds <= 0f ? 5f : dto.durationSeconds, 2f, 30f),
            reason = TextUtils.CleanShortText(dto.reason, 14),
            thought = CleanActivityThought(dto.thought, actionType,
                TextUtils.JoinParticipantNames(coworkers)),
            customActionLabel = TextUtils.CleanShortText(dto.customActionLabel, 24),
            energyChange = dto.energyChange,
            focusChange = dto.focusChange,
            socialChange = dto.socialChange,
            productivityChange = dto.productivityChange,
            socialMemorySubject = commitment != null ? commitment.subject : "",
            completesSocialCommitment = commitment != null,
            companionPreparation = companionPreparation,
            giveHeldItemToTarget = giveHeldItemToTarget
        };
    }

    private static bool IsBirthdayPreparation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !TextUtils.ContainsIgnoreCase(text, "birthday"))
            return false;
        return TextUtils.ContainsIgnoreCase(text, "gift")
            || TextUtils.ContainsIgnoreCase(text, "surprise")
            || TextUtils.ContainsIgnoreCase(text, "decorat");
    }

    private static string CleanActivityThought(string raw, OfficeActionType actionType,
        string participantNames)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        string line = raw.Trim().Trim('"', '\'', '\u201c', '\u201d', '\u2018', '\u2019').Trim();
        if (string.IsNullOrWhiteSpace(line))
            return "";

        line = TextUtils.StripSpeakerLabels(line, "", participantNames);
        line = TextUtils.StripReplyLabel(line);
        if (string.IsNullOrWhiteSpace(line))
            return "";

        if (TextUtils.IsAssistantStyleReply(line))
            return "";

        string lower = line.Trim().ToLowerInvariant();
        if (!EndsWithSentencePunctuation(line) && LooksLikeQuestion(lower))
            line = line.TrimEnd(',', ';', ':', '-') + "?";

        string complete = TextUtils.KeepCompleteThought(line, out _);
        if (string.IsNullOrWhiteSpace(complete))
            return "";

        int maxWords = actionType == OfficeActionType.ApproachColleague ? 12 : 10;
        int words = TextUtils.CountWords(complete);
        if (words == 0 || words > maxWords)
            return "";

        if (actionType == OfficeActionType.ApproachColleague
            && EndsWithIncompleteRequest(lower))
            return "";

        return complete;
    }

    private static bool EndsWithSentencePunctuation(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        char final = line.TrimEnd()[line.TrimEnd().Length - 1];
        return final == '.' || final == '!' || final == '?';
    }

    private static bool LooksLikeQuestion(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;
        string[] openers =
        {
            "what ", "why ", "how ", "when ", "where ", "who ",
            "do ", "does ", "did ", "can ", "could ", "would ", "will ",
            "should ", "is ", "are ", "have ", "has "
        };
        foreach (string opener in openers)
            if (lower.StartsWith(opener, StringComparison.Ordinal))
                return true;
        return lower.Contains(" can you ") || lower.Contains(" could you ")
            || lower.Contains(" would you ");
    }

    private static bool EndsWithIncompleteRequest(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;
        string value = lower.Trim().TrimEnd('.', '!', '?', ',', ';', ':');
        string[] endings =
        {
            "can you share", "could you share", "would you share",
            "can you explain", "could you explain", "would you explain",
            "can you help", "could you help", "would you help"
        };
        foreach (string ending in endings)
            if (value.EndsWith(ending, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void NormalizeActivitySequences(List<OfficeActivityPlan> plans)
    {
        if (plans == null)
            return;

        int index = 0;
        while (index < plans.Count)
        {
            OfficeActivityPlan first = plans[index];
            if (first == null || string.IsNullOrWhiteSpace(first.sequenceId))
            {
                ClearSequence(first);
                index++;
                continue;
            }

            int end = index + 1;
            while (end < plans.Count && plans[end] != null
                && string.Equals(plans[end].sequenceId, first.sequenceId,
                    StringComparison.OrdinalIgnoreCase))
                end++;

            bool valid = end - index >= 2 && !string.IsNullOrWhiteSpace(first.objective);
            for (int i = index; valid && i < end; i++)
            {
                valid = plans[i].sequenceStep == i - index + 1
                    && string.Equals(plans[i].objective, first.objective,
                        StringComparison.OrdinalIgnoreCase);
            }

            bool obtainedItem = false;
            for (int i = index; valid && i < end; i++)
            {
                obtainedItem |= plans[i].actionType == OfficeActionType.VendingMachine
                    || plans[i].actionType == OfficeActionType.CoffeeMachine;
                if (plans[i].giveHeldItemToTarget && !obtainedItem)
                    valid = false;
            }

            if (!valid)
                for (int i = index; i < end; i++)
                {
                    plans[i].giveHeldItemToTarget = false;
                    ClearSequence(plans[i]);
                }
            index = end;
        }
    }

    private static void ClearSequence(OfficeActivityPlan plan)
    {
        if (plan == null)
            return;
        plan.sequenceId = "";
        plan.sequenceStep = 0;
        plan.objective = "";
    }

    private static SocialMemoryEntry FindOpenSocialMemory(AgentProfile profile, string candidate)
    {
        if (profile == null || string.IsNullOrWhiteSpace(candidate))
            return null;
        for (int i = profile.socialMemory.Count - 1; i >= 0; i--)
        {
            SocialMemoryEntry entry = profile.socialMemory[i];
            if (entry != null && entry.status == "open"
                && TextUtils.TextSimilarity(entry.subject, candidate) >= 0.7f)
                return entry;
        }
        return null;
    }

    // JsonUtility assigns these fields through reflection.
#pragma warning disable CS0649
    [Serializable]
    private class ActivityPlanBatchDTO
    {
        public ActivityPlanDTO[] activities;
    }

    [Serializable]
    private class ActivityPlanDTO
    {
        public string actionType;
        public string destinationMode;
        public string destinationHint;
        public string sequenceId;
        public int sequenceStep;
        public string objective;
        public string targetAgent;
        public float durationSeconds;
        public string reason;
        public string thought;
        public string customActionLabel;
        public float energyChange;
        public float focusChange;
        public float socialChange;
        public float productivityChange;
        public string socialMemorySubject;
        public string companionPreparation;
        public bool giveHeldItemToTarget;
    }
#pragma warning restore CS0649
}
