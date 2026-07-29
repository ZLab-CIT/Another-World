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
        Debug.Log("[Commitment recovery] queued " + commitments.Count +
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
            EnsureTravelActivity(result, availableActions, maxPlans);
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureTravelActivity(List<OfficeActivityPlan> plans,
        List<OfficeActionType> availableActions, int capacity)
    {
        if (plans == null || plans.Exists(RequiresTravel)
            || availableActions == null
            || !availableActions.Contains(OfficeActionType.WalkAround))
            return;

        plans.Insert(0, new OfficeActivityPlan
        {
            actionType = OfficeActionType.WalkAround,
            destinationMode = OfficeDestinationMode.FreePosition,
            destinationHint = "General",
            durationSeconds = 4f,
            reason = "stretch legs around the office"
        });
        while (plans.Count > Mathf.Max(1, capacity))
            plans.RemoveAt(plans.Count - 1);
    }

    private static bool RequiresTravel(OfficeActivityPlan plan)
    {
        if (plan == null)
            return false;
        if (plan.actionType == OfficeActionType.PhoneCall
            || plan.actionType == OfficeActionType.CheckPhone)
            return false;
        if (plan.actionType == OfficeActionType.WorkDesk)
            return false;
        if (plan.actionType == OfficeActionType.Think)
            return plan.destinationMode == OfficeDestinationMode.FreePosition;
        if (plan.actionType == OfficeActionType.Custom)
            return plan.destinationMode == OfficeDestinationMode.FreePosition;
        return true;
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
                + "people for roughly one quarter of conversations when their current states "
                + "suggest they are gathered or sharing a social area. Two-person conversations "
                + "have 3-4 lines; group conversations have 4-7 lines, every participant speaks, "
                + "and lines respond to each other. Start with the actual observation or question, "
                + "not a participant's name. No greetings, filler, exposition, "
                + "assistant language, repeated catchphrases, or numeric stat readouts. "
                + "Match the visible mood; use anger only for genuine conflict. "
                + "A phone call has one person and 2-3 one-sided lines to someone outside this "
                + "office; never call or address a listed coworker. Actions with timing before "
                + "happen before dialogue; actions with timing after happen after it. If a line "
                + "claims an immediate action such as 'I'll check now', include a matching after "
                + "action for that speaker, otherwise do not say it. "
                + "Valid action types in this scene: " + availableActionNames + ". "
                + "Use VendingMachine for snacks and CoffeeMachine for coffee. "
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
                + "\"area\":\"General|Quiet|Lounge|WorkArea|Corridor\",\"seconds\":number,"
                + "\"thought\":string,\"reason\":string}],"
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
            maxTokens = Mathf.Clamp(requestedCount * 260, 1500, 2400),
            jsonMode = true,
            reasoningEffort = "low",
            excludeReasoning = true,
            timeoutSeconds = Mathf.Clamp(timeoutSeconds, 15, 45),
            maxRetries = 2,
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
        int minimumCount = Mathf.Min(requestedCount, 3);
        int conversationCount = valid.FindAll(beat =>
            string.Equals(beat.kind, "conversation",
                StringComparison.OrdinalIgnoreCase)).Count;
        if (valid.Count < minimumCount
            || conversationCount < 2
            || conversationCount * 10 < valid.Count * 7)
            return null;

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
            && HasUnbackedImmediateAction(lines, actions))
            return null;
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
            schemaVersion = 3,
            beatId = beatId,
            kind = kind,
            topic = topic,
            delaySeconds = Mathf.Clamp(
                raw.delay > 0f ? raw.delay : 40f, 30f, 50f),
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
            if (ContainsStatReadout(thought))
                thought = "";
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
            OfficeActionType.CheckPhone
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
        int allowedGroups = Mathf.Max(1, (conversationTotal + 1) / 2);
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

    private static bool HasUnbackedImmediateAction(
        List<OfficeEpisodeDialogueLine> lines,
        List<OfficeEpisodeAction> actions)
    {
        foreach (OfficeEpisodeDialogueLine line in lines)
        {
            if (!ClaimsImmediateAction(line?.line))
                continue;
            bool backed = actions.Exists(action => action != null
                && string.Equals(action.agentId, line.agentId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(action.timing, "after",
                    StringComparison.OrdinalIgnoreCase));
            if (!backed)
                return true;
        }
        return false;
    }

    private static bool ClaimsImmediateAction(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        string lower = line.ToLowerInvariant();
        bool promise = lower.Contains("i'll ")
            || lower.Contains("i will ")
            || lower.Contains("let me ");
        bool immediate = lower.Contains("right now")
            || lower.Contains(" now")
            || lower.Contains("immediately")
            || lower.Contains("straight away");
        if (!promise || !immediate)
            return false;
        string[] actionWords =
        {
            "check", "look", "go", "get", "grab", "bring", "move",
            "post", "call", "print", "fix", "deliver", "hand"
        };
        return Array.Exists(actionWords, lower.Contains);
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
        string line = CleanSentence(raw, 24);
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
