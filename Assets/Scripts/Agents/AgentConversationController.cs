using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(AIWorkerAgent))]
public class AgentConversationController : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float participantRadius = 1.5f;
    [SerializeField, Min(1)] private int maxTurns = 6;

    private readonly Dictionary<string, int> affinity = new();
    private AIWorkerAgent owner;
    private float nextSocialCheckTime;
    private bool inConversation;

    private void Awake()
    {
        owner = GetComponent<AIWorkerAgent>();
    }

    public void OnStartedActing(OfficeActionPoint action, string openerHint)
    {
        nextSocialCheckTime = Time.time + 0.5f;
        TryStart(action, openerHint);
    }

    public void Tick(OfficeActionPoint action, string openerHint)
    {
        if (action == null || !IsSocialSpot(action.actionType) || Time.time < nextSocialCheckTime)
            return;

        nextSocialCheckTime = Time.time + 0.7f;
        TryStart(action, openerHint);
    }

    public string BuildRelationships()
    {
        string result = "";
        int count = 0;
        foreach (KeyValuePair<string, int> relationship in affinity)
        {
            if (relationship.Value <= 0 || count >= 4)
                continue;

            result += (result.Length > 0 ? ", " : "") + relationship.Key + "(" + relationship.Value + ")";
            count++;
        }

        return result;
    }

    public AIWorkerAgent FindWorkerByDisplayName(string displayName)
    {
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null || string.IsNullOrEmpty(displayName))
            return null;

        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker != null && worker != owner
                && string.Equals(worker.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                return worker;
        }

        return null;
    }

    private void SetCooldown(float seconds)
    {
        nextSocialCheckTime = Mathf.Max(nextSocialCheckTime, Time.time + seconds);
    }

    private void TryStart(OfficeActionPoint action, string openerHint)
    {
        if (inConversation || action == null || !IsSocialSpot(action.actionType))
            return;

        List<AIWorkerAgent> participants = FindNearbyParticipants(participantRadius);
        if (participants.Count == 0)
            return;

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null || !brain.EnableSocialReplies || !IsConversationStarter(participants))
            return;

        if (!string.IsNullOrEmpty(openerHint))
            BeginConversation(participants, openerHint);
        else
            BeginGeneratedConversation(participants);
    }

    private bool IsConversationStarter(List<AIWorkerAgent> participants)
    {
        if (!owner.UseLLMBrain)
            return false;

        int ownerId = owner.GetInstanceID();
        foreach (AIWorkerAgent participant in participants)
        {
            if (participant != null && participant.UseLLMBrain && participant.GetInstanceID() < ownerId)
                return false;
        }

        return true;
    }

    private void BeginConversation(List<AIWorkerAgent> participants, string line)
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

        ExtendAll(participants, 14f);
        owner.ShowThought($"{owner.DisplayName} says:\n{line}");
        owner.ApplyEffects(0f, 0f, 6f, 0f);

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
        {
            EndConversation(participants);
            return;
        }

        foreach (AIWorkerAgent participant in participants)
            if (participant != null)
                brain.Remember(participant.AgentId, owner.DisplayName + " said: " + line);

        RunConversation(participants, line);
    }

    private void BeginGeneratedConversation(List<AIWorkerAgent> participants)
    {
        string opener = participants.Count > 0 && participants[0] != null
            ? "Hey " + participants[0].DisplayName + ", how's your day going?"
            : "Anyone want to chat for a minute?";
        BeginConversation(participants, opener);
    }

    private async void RunConversation(List<AIWorkerAgent> participants, string openerLine)
    {
        LLMBrainService brain = LLMBrainService.Instance;
        List<AIWorkerAgent> speakers = new() { owner };
        foreach (AIWorkerAgent participant in participants)
            if (participant != null && participant != owner)
                speakers.Add(participant);

        string names = BuildParticipantNames(speakers);
        string lastSpeaker = owner.DisplayName;
        string lastLine = openerLine;

        try
        {
            for (int turn = 1; turn < maxTurns; turn++)
            {
                AIWorkerAgent speaker = speakers[turn % speakers.Count];
                if (speaker == null || NeedsToLeave(speaker))
                    break;

                string line = brain != null
                    ? await brain.ConverseAsync(speaker.AgentId, names, lastSpeaker, lastLine)
                    : null;

                if (string.IsNullOrEmpty(line))
                {
                    SetConversationCooldown(speakers, 8f);
                    line = IsQuestionLine(lastLine)
                        ? BuildFallbackQuestionReply(lastSpeaker)
                        : BuildFallbackConversationReply(lastSpeaker);
                }

                if (string.IsNullOrEmpty(line))
                    break;

                speaker.ShowThought($"{speaker.DisplayName} says:\n{line}");
                speaker.ApplyEffects(0f, 0f, 5f, 0f);
                GetController(speaker)?.IncrementAffinity(lastSpeaker);
                ExtendAll(speakers, 12f, includeOwner: false);

                if (brain != null)
                    brain.Remember(speaker.AgentId, lastSpeaker + ": " + lastLine);

                lastSpeaker = speaker.DisplayName;
                lastLine = line;
            }
        }
        catch
        {
            // A failed turn is non-critical.
        }
        finally
        {
            EndConversation(participants);
        }
    }

    private List<AIWorkerAgent> FindNearbyParticipants(float radius)
    {
        List<AIWorkerAgent> result = new();
        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null)
            return result;

        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker == null || worker == owner)
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

    private static bool NeedsToLeave(AIWorkerAgent agent)
    {
        return agent == null || agent.energy < 30f || agent.focus < 30f;
    }

    private static bool IsQuestionLine(string line)
    {
        return !string.IsNullOrEmpty(line) && line.IndexOf('?') >= 0;
    }

    private static string BuildFallbackQuestionReply(string lastSpeaker)
    {
        return string.IsNullOrEmpty(lastSpeaker)
            ? "I'm doing alright, thanks."
            : "I'm doing alright, thanks for asking, " + lastSpeaker + ".";
    }

    private static string BuildFallbackConversationReply(string lastSpeaker)
    {
        return string.IsNullOrEmpty(lastSpeaker)
            ? "Sorry, I got distracted for a second."
            : "Sorry, " + lastSpeaker + ", I got distracted for a second.";
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
