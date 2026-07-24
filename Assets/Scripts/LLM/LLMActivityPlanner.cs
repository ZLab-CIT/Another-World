using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class LLMActivityPlanner
{
    private readonly LLMBrainService brain;
    private readonly ILLMBackend backend;

    public LLMActivityPlanner(LLMBrainService brain, ILLMBackend backend)
    {
        this.brain = brain;
        this.backend = backend;
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
        OfficeActivityPlan commitmentPlan = BuildOpenCommitmentPlan(
            profile, availableActions, coworkers);
        if (commitmentPlan != null
            && (TextUtils.ContainsIgnoreCase(commitmentPlan.socialMemorySubject, "coffee")
                || TextUtils.ContainsIgnoreCase(commitmentPlan.socialMemorySubject, "drink")))
            return SinglePlan(commitmentPlan);

        if (backend == null)
            return SinglePlan(commitmentPlan);

        if (brain.SocialRequestGate.CurrentCount == 0 || brain.PendingConversationScripts > 0)
            return SinglePlan(commitmentPlan);

        await brain.SocialRequestGate.WaitAsync();
        try
        {
            string who = TextUtils.DisplayName(profile, agentId);
            string allowed = TextUtils.JoinActionTypes(availableActions);
            string coworkerNames = TextUtils.JoinParticipantNames(coworkers);
            StringBuilder context = new();
            if (!string.IsNullOrWhiteSpace(currentState))
                context.Append("Current state: ").Append(currentState.Trim()).AppendLine();
            TextUtils.AppendRecentMemory(context, profile, Mathf.Max(2, memoryLines));
            TextUtils.AppendSocialMemory(context, profile, Mathf.Max(2, memoryLines));
            TextUtils.AppendWorldEvents(context, brain.WorldEvents, 3);

            List<ChatMessage> messages = new()
            {
                new ChatMessage("system",
                    "Plan the next " + requestedCount + " believable physical office activities for a simulation worker. You are " + who + ". " +
                    profile.personality + " Create a short, varied sequence that fits their needs, personality, and recent context. " +
                    "When practical, honor an unresolved promise, favor, invitation, or plan from social memory. " +
                    "Do not repeat the same action consecutively or fill the sequence with desk work. " +
                    "When one objective needs multiple physical steps, make 2 to 4 consecutive activities a linked sequence. " +
                    "Give those activities the same short sequenceId and objective, with sequenceStep starting at 1 and increasing by 1. " +
                    "Example: walk to printer, use printer, then return to desk. Leave sequenceId and objective empty and sequenceStep 0 for independent activities. " +
                    "ActionPoint means an exact object such as a desk, printer, or coffee machine. " +
                    "FreePosition means a reachable place in the office. CurrentPosition means no travel. " +
                    "FollowAgent means approach the named coworker's live position; use it only for ApproachColleague. " +
                    "You may use Custom as actionType to invent a new custom action not tied to any scene object; set customActionLabel to a short description (e.g., \"stretch arms\", \"look out window\"). " +
                    "Custom actions cannot create, fetch, carry, give, or transfer objects. Do not plan snacks, food, presents, birthday gifts, or other unavailable items unless the action is VendingMachine. " +
                    "Custom actions use FreePosition or CurrentPosition as destinationMode. " +
                    "Optionally set energyChange, focusChange, socialChange, productivityChange (negative to decrease, positive to increase, default 0) to describe need effects. " +
                    "If an activity physically fulfills one open social-memory commitment, copy that memory's subject exactly into socialMemorySubject; otherwise use an empty string. " +
                    "For an invitation, choose BreakSpot or ChatSpot, target the invited coworker, and write a specific in-character socialOpeningLine that refers naturally to the remembered plan. " +
                    "socialOpeningLine must use simple everyday English and contain one sentence of 4 to 12 words with one clear idea. " +
                    "Respond only as JSON with exactly one activities array: " +
                    "{\"activities\":[{\"actionType\":string,\"destinationMode\":string,\"destinationHint\":string," +
                    "\"sequenceId\":string,\"sequenceStep\":number,\"objective\":string," +
                    "\"targetAgent\":string,\"durationSeconds\":number,\"reason\":string,\"thought\":string," +
                    "\"customActionLabel\":string,\"energyChange\":number,\"focusChange\":number,\"socialChange\":number,\"productivityChange\":number,\"socialMemorySubject\":string,\"socialOpeningLine\":string}" +
                    "]}. Return exactly " + requestedCount + " activity objects. " +
                    "actionType must be one of: " + allowed + ", or Custom. " +
                    "destinationMode must be ActionPoint, FreePosition, CurrentPosition, or FollowAgent. " +
                    "For a free destination, destinationHint can be General, Quiet, Lounge, WorkArea, or Corridor. " +
                    (string.IsNullOrWhiteSpace(coworkerNames)
                        ? "No coworkers are currently available, so do not choose ApproachColleague. "
                        : "For ApproachColleague, targetAgent must be exactly one of: " + coworkerNames + ". ") +
                    "Use ApproachColleague when asking that coworker for advice, help, or a discussion. For ApproachColleague, thought must be the exact short sentence spoken on arrival, not an internal thought. " +
                    "durationSeconds must be between 2 and 30. " +
                    "reason is 3 to 14 words. thought is an optional short in-character thought, 0 to 12 words."),
                new ChatMessage("user", context.ToString())
            };

            LLMOptions options = new()
            {
                requestLabel = "ActivityBatch:" + who,
                temperature = Mathf.Clamp(temperature, 0.55f, 0.8f),
                maxTokens = Mathf.Clamp(requestedCount * 150, 300, 800),
                jsonMode = true,
                structuredSchema = LLMJsonSchema.ActivityPlan,
                timeoutSeconds = Mathf.Min(requestTimeoutSeconds,
                    Mathf.Max(8, activityPlanTimeoutSeconds)),
                maxRetries = 2,
                retryBaseDelaySeconds = 5f
            };
            string raw = await backend.CompleteAsync(messages, options);
            List<OfficeActivityPlan> plans = ParseActivityPlans(
                raw, availableActions, coworkers, requestedCount, profile);
            plans = PrependCommitment(plans, commitmentPlan, requestedCount);
            if (commitmentPlan != null && plans != null && plans.Count > 0)
            {
                OfficeActivityPlan selectedCommitment = plans[0];
                bool modelSelected = !ReferenceEquals(selectedCommitment, commitmentPlan);
                Debug.Log("[Social commitment plan] " + who + ": " +
                    (modelSelected ? "model chose " : "fallback chose ") +
                    selectedCommitment.actionType +
                    (string.IsNullOrWhiteSpace(selectedCommitment.targetAgent)
                        ? "" : " with " + selectedCommitment.targetAgent));
            }
            if ((plans == null || plans.Count == 0) && !string.IsNullOrWhiteSpace(raw))
                Debug.LogWarning("[Activity batch rejected] " + who +
                    ": no valid available activities");
            else if (plans != null && plans.Count > 0)
                Debug.Log("[Activity batch] " + who + ": accepted " + plans.Count +
                    " queued activities");
            return plans;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(LLMBrainService) + " activity planning failed for " +
                agentId + ": " + exception.Message);
            return SinglePlan(commitmentPlan);
        }
        finally
        {
            brain.SocialRequestGate.Release();
        }
    }

    private static List<OfficeActivityPlan> SinglePlan(OfficeActivityPlan plan)
    {
        return plan == null ? null : new List<OfficeActivityPlan> { plan };
    }

    private static List<OfficeActivityPlan> PrependCommitment(List<OfficeActivityPlan> plans,
        OfficeActivityPlan commitment, int capacity)
    {
        if (commitment == null)
            return plans;
        plans ??= new List<OfficeActivityPlan>();
        for (int i = 0; i < plans.Count; i++)
        {
            if (plans[i] == null || string.IsNullOrWhiteSpace(plans[i].socialMemorySubject))
                continue;
            OfficeActivityPlan generatedCommitment = plans[i];
            plans.RemoveAt(i);
            plans.Insert(0, generatedCommitment);
            while (plans.Count > Mathf.Max(1, capacity))
                plans.RemoveAt(plans.Count - 1);
            return plans;
        }
        plans.Insert(0, commitment);
        while (plans.Count > Mathf.Max(1, capacity))
            plans.RemoveAt(plans.Count - 1);
        return plans;
    }

    private static OfficeActivityPlan BuildOpenCommitmentPlan(AgentProfile profile,
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
                return new OfficeActivityPlan
                {
                    actionType = socialType.Value,
                    destinationMode = OfficeDestinationMode.ActionPoint,
                    targetAgent = target.displayName,
                    durationSeconds = 8f,
                    reason = "follow through on the invitation",
                    thought = "I should keep that invitation.",
                    socialMemorySubject = entry.subject,
                    completesSocialCommitment = true
                };
            }

            if (entry.type == "promise" || entry.type == "favor_request" || entry.type == "plan")
            {
                bool coffeeDelivery = TextUtils.ContainsIgnoreCase(entry.subject, "coffee")
                    || TextUtils.ContainsIgnoreCase(entry.subject, "drink");
                if (coffeeDelivery)
                {
                    ConversationParticipantContext target = TextUtils.FindParticipant(coworkers, entry.targetAgent);
                    if (target == null)
                        continue;
                    AIWorkerAgent actorAgent = FindLiveAgent(actor);
                    if (actorAgent != null && actorAgent.IsHolding)
                    {
                        return new OfficeActivityPlan
                        {
                            actionType = OfficeActionType.ApproachColleague,
                            destinationMode = OfficeDestinationMode.FollowAgent,
                            targetAgent = target.displayName,
                            durationSeconds = 2f,
                            reason = "deliver the promised coffee",
                            thought = "I should deliver this coffee.",
                            socialMemorySubject = entry.subject,
                            completesSocialCommitment = true
                        };
                    }
                    if (!availableActions.Contains(OfficeActionType.CoffeeMachine))
                        continue;
                    return new OfficeActivityPlan
                    {
                        actionType = OfficeActionType.CoffeeMachine,
                        destinationMode = OfficeDestinationMode.ActionPoint,
                        durationSeconds = 3f,
                        reason = "get the promised coffee",
                        thought = "I promised to bring coffee.",
                        socialMemorySubject = entry.subject,
                        completesSocialCommitment = false
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
                return new OfficeActivityPlan
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
                };
            }
        }
        return null;
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
            if (TextUtils.ContainsUnavailableObjectClaim(customDescription))
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
        if (requiresCoworker && TextUtils.FindParticipant(coworkers, targetAgent) == null)
            return null;

        SocialMemoryEntry commitment = FindOpenSocialMemory(profile, dto.socialMemorySubject);
        bool socialAction = actionType == OfficeActionType.BreakSpot
            || actionType == OfficeActionType.ChatSpot;
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
            if (!validInteraction)
            {
                Debug.LogWarning("[Commitment action rejected] "
                    + TextUtils.DisplayName(profile, profile.agentId) + ": action did not physically fulfill: "
                    + commitment.subject);
                commitment = null;
            }
        }

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
            thought = TextUtils.CleanShortText(dto.thought, 12),
            customActionLabel = TextUtils.CleanShortText(dto.customActionLabel, 24),
            energyChange = dto.energyChange,
            focusChange = dto.focusChange,
            socialChange = dto.socialChange,
            productivityChange = dto.productivityChange,
            socialMemorySubject = commitment != null ? commitment.subject : "",
            completesSocialCommitment = commitment != null,
            socialOpeningLine = commitment != null && socialAction
                ? TextUtils.CleanShortText(dto.socialOpeningLine, 20) : ""
        };
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

            if (!valid)
                for (int i = index; i < end; i++)
                    ClearSequence(plans[i]);
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
        public string socialOpeningLine;
    }
}
