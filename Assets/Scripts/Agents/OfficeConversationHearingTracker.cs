using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public sealed class OfficeConversationHearingRecord
{
    public string conversationId;
    public Vector2 center;
    public string topic;
    public List<string> speakerNames;
    public List<string> spokenLines;
    public float endsAt;
    public bool ended;
}

public static class OfficeConversationHearingTracker
{
    public static readonly List<OfficeConversationHearingRecord> ActiveConversations = new();

    public static string RegisterConversation(Vector2 center, string topic,
        List<AIWorkerAgent> speakers)
    {
        var record = new OfficeConversationHearingRecord();
        PruneExpired();
        record.conversationId = System.Guid.NewGuid().ToString("N");
        record.center = center;
        record.topic = topic ?? "";
        record.speakerNames = new List<string>();
        record.spokenLines = new List<string>();
        if (speakers != null)
            foreach (AIWorkerAgent speaker in speakers)
            {
                if (speaker == null)
                    continue;
                record.speakerNames.Add(speaker.DisplayName);
            }
        record.endsAt = Time.time + 45f;
        ActiveConversations.Add(record);
        return record.conversationId;
    }

    public static void AppendLine(string conversationId, string line)
    {
        OfficeConversationHearingRecord record = Find(conversationId);
        if (record == null || string.IsNullOrWhiteSpace(line))
            return;
        if (record.spokenLines.Count >= 12)
            record.spokenLines.RemoveAt(0);
        record.spokenLines.Add(line.Trim());
    }

    public static bool IsActive(string conversationId)
    {
        OfficeConversationHearingRecord record = Find(conversationId);
        return record != null && !record.ended;
    }

    public static void End(string conversationId)
    {
        OfficeConversationHearingRecord record = Find(conversationId);
        if (record == null)
            return;
        record.ended = true;
    }

    public static void Remove(string conversationId)
    {
        for (int i = ActiveConversations.Count - 1; i >= 0; i--)
            if (ActiveConversations[i] != null
                && string.Equals(ActiveConversations[i].conversationId, conversationId,
                    System.StringComparison.OrdinalIgnoreCase))
                ActiveConversations.RemoveAt(i);
    }

    public static OfficeConversationHearingRecord Get(string conversationId)
    {
        return Find(conversationId);
    }

    public static void ClearAll()
    {
        ActiveConversations.Clear();
    }

    public static OfficeConversationHearingRecord FindNearestActive(Vector2 from,
        float maxRadius)
    {
        PruneExpired();
        float bestDistance = float.MaxValue;
        OfficeConversationHearingRecord best = null;
        foreach (OfficeConversationHearingRecord record in ActiveConversations)
        {
            if (record == null || record.ended)
                continue;
            float distance = Vector2.Distance(from, record.center);
            if (distance > maxRadius || distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = record;
        }
        return best;
    }

    private static void PruneExpired()
    {
        for (int i = ActiveConversations.Count - 1; i >= 0; i--)
        {
            OfficeConversationHearingRecord record = ActiveConversations[i];
            if (record == null || Time.time > record.endsAt)
                ActiveConversations.RemoveAt(i);
        }
    }

    private static OfficeConversationHearingRecord Find(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;
        foreach (OfficeConversationHearingRecord record in ActiveConversations)
            if (record != null && string.Equals(record.conversationId, conversationId,
                    System.StringComparison.OrdinalIgnoreCase))
                return record;
        return null;
    }
}

public static class OfficePersonalRevealDetector
{
    public static string TryCatchReveal(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";
        string lower = line.ToLowerInvariant();
        if (ContainsAny(lower,
                "my birthday", "birthday is today", "it's my birthday",
                "today is my birthday", "birthday party"))
            return "birthday";
        if (ContainsAny(lower,
                "we broke up", "breakup", "break up", "broke up",
                "she left me", "he left me", "my partner and i split",
                "i got dumped"))
            return "breakup";
        if (ContainsAny(lower,
                "going on a date", "i have a date", "asked me out",
                "i've been seeing", "new partner", "my crush asked"))
            return "date";
        if (ContainsAny(lower,
                "i got promoted", "i got the job", "accepted the offer",
                "my contract was renewed", "congr", "tell you my news"))
            return "good news";
        if (ContainsAny(lower,
                "rejected me", "turned me down", "said no to me", "got fired",
                "i lost my", "my grandmother is", "in hospital", "feel terrible today"))
            return "bad news";
        return "";
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (string value in values)
            if (text.Contains(value))
                return true;
        return false;
    }
}