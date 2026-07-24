using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public static class TextUtils
{
    public static string NormalizeForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        StringBuilder result = new();
        bool previousSpace = false;
        foreach (char character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(character);
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                result.Append(' ');
                previousSpace = true;
            }
        }
        return result.ToString().Trim();
    }

    public static HashSet<string> MeaningfulWords(string normalized)
    {
        HashSet<string> result = new();
        string[] ignored = { "the", "a", "an", "and", "or", "but", "i", "you", "we", "it",
            "is", "are", "was", "were", "to", "of", "in", "on", "for", "that", "this", "my",
            "your", "our", "with", "have", "has", "had", "do", "did", "what", "how" };
        HashSet<string> stopWords = new(ignored);
        foreach (string word in normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (word.Length > 1 && !stopWords.Contains(word))
                result.Add(StemWord(word));
        return result;
    }

    public static string StemWord(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
            return word.Substring(0, word.Length - 3);
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
            return word.Substring(0, word.Length - 2);
        if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal))
            return word.Substring(0, word.Length - 1);
        return word;
    }

    public static float TextSimilarity(string first, string second)
    {
        string a = NormalizeForComparison(first);
        string b = NormalizeForComparison(second);
        if (a.Length == 0 || b.Length == 0)
            return 0f;
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 1f;

        HashSet<string> aWords = MeaningfulWords(a);
        HashSet<string> bWords = MeaningfulWords(b);
        if (aWords.Count == 0 || bWords.Count == 0)
            return 0f;
        int overlap = 0;
        foreach (string word in aWords)
            if (bWords.Contains(word))
                overlap++;
        return overlap / (float)Mathf.Min(aWords.Count, bWords.Count);
    }

    public static int CountWords(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? 0 : value.Split(
            new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string value = raw.Trim();
        int start = value.IndexOf('{');
        int end = value.LastIndexOf('}');
        return start >= 0 && end > start ? value.Substring(start, end - start + 1) : null;
    }

    public static int CountOccurrences(string value, string fragment)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(fragment))
            return 0;
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }
        return count;
    }

    public static bool ContainsIgnoreCase(string value, string fragment)
    {
        return !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(fragment)
            && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool ContainsUnavailableObjectClaim(string value)
    {
        string[] unavailable =
        {
            "gift", "present", "snack", "food", "cake", "dumpling"
        };
        foreach (string word in unavailable)
            if (ContainsIgnoreCase(value, word))
                return true;
        return false;
    }

    public static ConversationParticipantContext FindParticipant(
        List<ConversationParticipantContext> participants, string displayName)
    {
        if (participants == null || string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (ConversationParticipantContext participant in participants)
            if (participant != null && string.Equals(participant.displayName, displayName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return participant;
        return null;
    }

    public static void AddRecent(List<string> values, string value, int capacity)
    {
        if (values == null || string.IsNullOrWhiteSpace(value))
            return;
        values.Add(value.Trim());
        while (values.Count > Mathf.Max(1, capacity))
            values.RemoveAt(0);
    }

    public static bool IsSimilarToAny(string candidate, List<string> previous, float threshold)
    {
        if (string.IsNullOrWhiteSpace(candidate) || previous == null)
            return false;
        foreach (string value in previous)
            if (TextSimilarity(candidate, value) >= threshold)
                return true;
        return false;
    }

    public static bool HasMeaningfulOverlap(string subject, string transcript)
    {
        HashSet<string> subjectWords = MeaningfulWords(NormalizeForComparison(subject));
        HashSet<string> transcriptWords = MeaningfulWords(NormalizeForComparison(transcript));
        foreach (string word in subjectWords)
            if (transcriptWords.Contains(word))
                return true;
        return false;
    }

    public static string CleanShortText(string value, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim()
            .Trim('"', '\'', '.', '!', '?', ':', ';');
        string[] words = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "";
        if (words.Length <= maxWords)
            return clean;

        StringBuilder result = new();
        for (int i = 0; i < maxWords; i++)
        {
            if (result.Length > 0)
                result.Append(' ');
            result.Append(words[i]);
        }
        return result.ToString();
    }

    public static string CleanTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return null;
        string value = topic.Replace('\r', ' ').Replace('\n', ' ').Trim()
            .Trim('"', '\'', '.', '!', '?', ':', ';');
        int words = CountWords(value);
        return words >= 2 && words <= 18 ? value : null;
    }

    public static string CleanReply(string raw, string speakerName, string participants,
        out string rejectionReason)
    {
        rejectionReason = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            rejectionReason = "empty response";
            return null;
        }

        string s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0)
                s = s.Substring(firstNewline + 1);
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                s = s.Substring(0, fence);
            s = s.Trim();
        }

        bool hasUnclosedQuote = HasUnclosedDialogueQuote(s);

        int firstQuote = s.IndexOf('"');
        int lastQuote = s.LastIndexOf('"');
        if (firstQuote >= 0 && lastQuote > firstQuote)
            s = s.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Trim();

        s = SelectSpokenLine(s);
        if (string.IsNullOrWhiteSpace(s))
        {
            rejectionReason = "no usable spoken line";
            return null;
        }

        s = StripSpeakerLabels(s, speakerName, participants);
        s = StripReplyLabel(s);
        s = s?.Trim().Trim('"', '\'', '\u201c', '\u201d', '\u2018', '\u2019').Trim();
        s = RemoveGenericAgreementOpening(s);
        if (IsAssistantStyleReply(s) || IsNarratedReply(ref s, speakerName, participants))
        {
            rejectionReason = string.IsNullOrWhiteSpace(s)
                ? "only a generic agreement opener"
                : "assistant-style or narrated response";
            return null;
        }

        s = KeepCompleteThought(s, hasUnclosedQuote, out rejectionReason);

        if (string.IsNullOrWhiteSpace(s))
            return null;

        return s;
    }

    public static string SelectSpokenLine(string text)
    {
        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim().TrimStart('-', '*').Trim();
            line = StripReplyLabel(line);
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (IsAssistantStyleReply(line))
                return null;
            return line;
        }

        return null;
    }

    public static string StripSpeakerLabels(string line, string speakerName, string participants)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        List<string> names = new();
        if (!string.IsNullOrWhiteSpace(speakerName))
            names.Add(speakerName.Trim());
        if (!string.IsNullOrWhiteSpace(participants))
        {
            string[] participantNames = participants.Split(',');
            foreach (string participantName in participantNames)
                if (!string.IsNullOrWhiteSpace(participantName))
                    names.Add(participantName.Trim());
        }

        string result = line.Trim();
        bool changed;
        do
        {
            changed = false;
            foreach (string name in names)
            {
                string label = name + ":";
                if (!result.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                    continue;

                result = result.Substring(label.Length).Trim();
                changed = true;
                break;
            }
        }
        while (changed && result.Length > 0);

        return result;
    }

    public static string StripReplyLabel(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        int colon = line.IndexOf(':');
        if (colon < 0)
            return line.Trim();

        string prefix = line.Substring(0, colon).Trim().ToLowerInvariant();
        bool isReplyLabel = prefix == "reply" || prefix == "response" || prefix == "answer"
            || prefix == "assistant" || prefix == "spoken line"
            || prefix == "content operator" || prefix == "designer" || prefix == "manager"
            || prefix == "software developer" || prefix == "human resources specialist"
            || prefix == "office assistant"
            || prefix.Contains("character") || prefix.Contains("next reply")
            || prefix.Contains("would say") || prefix.Contains("will say");

        return isReplyLabel ? line.Substring(colon + 1).Trim() : line.Trim();
    }

    public static string RemoveGenericAgreementOpening(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        string value = line.Trim();
        string lower = value.ToLowerInvariant();
        string[] genericOpeners =
        {
            "absolutely", "exactly", "definitely", "great idea", "you're right", "you are right",
            "i agree", "i completely agree", "i totally agree", "i understand", "of course",
            "that's great", "that sounds great", "sounds great", "thank you", "thanks"
        };

        bool matches = false;
        foreach (string opener in genericOpeners)
        {
            if (!lower.StartsWith(opener, StringComparison.Ordinal))
                continue;

            matches = true;
            break;
        }

        if (!matches)
            return value;

        int sentenceEnd = value.IndexOfAny(new[] { '.', '!', '?' });
        if (sentenceEnd >= 0 && sentenceEnd + 1 < value.Length)
            return value.Substring(sentenceEnd + 1).Trim();

        int separator = value.IndexOfAny(new[] { ',', ';', ':' });
        if (separator >= 0 && separator + 1 < value.Length)
            return value.Substring(separator + 1).Trim();

        return null;
    }

    public static bool HasUnclosedDialogueQuote(string text)
    {
        int straightQuotes = 0;
        foreach (char character in text)
            if (character == '"')
                straightQuotes++;

        return straightQuotes % 2 != 0
            || (text.IndexOf('\u201c') >= 0 && text.IndexOf('\u201d') < 0)
            || (text.IndexOf('\u2018') >= 0 && text.IndexOf('\u2019') < 0);
    }

    public static string KeepCompleteThought(string line, bool hasUnclosedQuote,
        out string rejectionReason)
    {
        rejectionReason = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            rejectionReason = "empty line after cleanup";
            return null;
        }
        if (hasUnclosedQuote)
        {
            rejectionReason = "generation ended inside a quotation";
            return null;
        }

        string value = line.Trim();
        int lastTerminal = LastTerminalPunctuation(value);
        if (lastTerminal >= 0 && lastTerminal < value.Length - 1)
        {
            string trailing = value.Substring(lastTerminal + 1).Trim(' ', '"', '\'', '\u201d', '\u2019');
            if (trailing.Length > 0)
                value = value.Substring(0, lastTerminal + 1).Trim();
        }

        string[] words = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            rejectionReason = "empty line after trimming";
            return null;
        }

        if (words.Length > 30)
        {
            string shorterSentence = SelectLongestCompleteSentence(value);
            if (!string.IsNullOrWhiteSpace(shorterSentence))
            {
                value = shorterSentence;
                words = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        char finalCharacter = value[value.Length - 1];
        if (finalCharacter == '.' || finalCharacter == '!' || finalCharacter == '?')
            return value;

        if (words.Length > 12)
        {
            rejectionReason = "response ended without punctuation after " + words.Length + " words";
            return null;
        }
        if (IsIncompleteFinalWord(words[words.Length - 1]))
        {
            rejectionReason = "response ended on an incomplete word: " + words[words.Length - 1];
            return null;
        }

        return value.TrimEnd(',', ';', ':', '-') + ".";
    }

    public static string SelectLongestCompleteSentence(string value)
    {
        string best = null;
        int bestWordCount = 0;
        int sentenceStart = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '.' && value[i] != '!' && value[i] != '?')
                continue;

            string sentence = value.Substring(sentenceStart, i - sentenceStart + 1)
                .Trim(' ', '"', '\'', '\u201c', '\u201d', '\u2018', '\u2019');
            int wordCount = sentence.Split(new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount > bestWordCount)
            {
                best = sentence;
                bestWordCount = wordCount;
            }

            sentenceStart = i + 1;
        }

        return best;
    }

    public static int LastTerminalPunctuation(string value)
    {
        for (int i = value.Length - 1; i >= 0; i--)
            if (value[i] == '.' || value[i] == '!' || value[i] == '?')
                return i;
        return -1;
    }

    public static bool IsIncompleteFinalWord(string word)
    {
        string value = word.Trim().Trim(',', ';', ':', '-', '"', '\'').ToLowerInvariant();
        switch (value)
        {
            case "a": case "an": case "the": case "and": case "but": case "or":
            case "to": case "for": case "from": case "with": case "of": case "at":
            case "in": case "on": case "by": case "while": case "because": case "that":
            case "which": case "who": case "what": case "how": case "your": case "my":
            case "our": case "their": case "you": case "i":
                return true;
            default:
                return false;
        }
    }

    public static bool IsAssistantStyleReply(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        string value = line.Trim().ToLowerInvariant();
        return value.StartsWith("as an ai", StringComparison.Ordinal)
            || value.StartsWith("as a language model", StringComparison.Ordinal)
            || value.StartsWith("i am an ai", StringComparison.Ordinal)
            || value.StartsWith("i'm an ai", StringComparison.Ordinal)
            || value.StartsWith("this character", StringComparison.Ordinal)
            || value.StartsWith("the character", StringComparison.Ordinal)
            || value.StartsWith("the assistant", StringComparison.Ordinal)
            || value.StartsWith("this assistant", StringComparison.Ordinal)
            || value.StartsWith("the next reply", StringComparison.Ordinal)
            || value.StartsWith("my response", StringComparison.Ordinal)
            || value.StartsWith("i cannot roleplay", StringComparison.Ordinal)
            || value.StartsWith("i can't roleplay", StringComparison.Ordinal);
    }

    public static bool IsNarratedReply(ref string line, string speakerName, string participants)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        string value = line.Trim().ToLowerInvariant();
        List<string> names = new();
        if (!string.IsNullOrWhiteSpace(speakerName))
            names.Add(speakerName.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(participants))
            foreach (string participant in participants.Split(','))
                if (!string.IsNullOrWhiteSpace(participant))
                    names.Add(participant.Trim().ToLowerInvariant());

        bool narrated = false;
        foreach (string name in names)
        {
            if (value.StartsWith(name + "'s ", StringComparison.Ordinal)
                || value.StartsWith(name + "\u2019s ", StringComparison.Ordinal)
                || value.StartsWith(name + " laughed", StringComparison.Ordinal)
                || value.StartsWith(name + " smiled", StringComparison.Ordinal)
                || value.StartsWith(name + " nodded", StringComparison.Ordinal)
                || value.Contains(name + " said ")
                || value.Contains(name + " replied "))
            {
                narrated = true;
                break;
            }
        }

        if (!narrated)
        {
            if (value.Contains("echoed through the office")
                || value.StartsWith("with a ", StringComparison.Ordinal)
                || (value.StartsWith("in a ", StringComparison.Ordinal) && value.Contains(" tone")))
                narrated = true;
        }

        if (!narrated)
            return false;

        string original = line.Trim();
        int sentenceEnd = original.IndexOfAny(new[] { '.', '!', '?' });
        if (sentenceEnd >= 0 && sentenceEnd + 1 < original.Length)
        {
            string extracted = original.Substring(sentenceEnd + 1).Trim();
            if (!string.IsNullOrWhiteSpace(extracted) && CountWords(extracted) >= 2)
            {
                line = extracted;
                return false;
            }
        }

        return true;
    }

    public static bool IsGenericTopic(string topic)
    {
        string value = NormalizeForComparison(topic);
        string[] generic =
        {
            "office conversation", "casual conversation", "general conversation",
            "daily life", "work life", "catching up", "how the day is going",
            "talking with coworkers", "friendly chat"
        };
        foreach (string phrase in generic)
            if (value == phrase || value.Contains(phrase))
                return true;
        return false;
    }

    public static bool IsGenericOpening(string opening)
    {
        string value = NormalizeForComparison(opening);
        string[] generic =
        {
            "how are you", "how is your day", "hows your day", "how are things",
            "what is up", "whats up", "got a minute", "do you have a minute",
            "want to chat", "can we chat", "how is work", "hows work",
            "how have you been", "what are you up to"
        };
        foreach (string phrase in generic)
            if (value.Contains(phrase))
                return true;
        return false;
    }

    public static bool LooksLikePersonName(string topic, List<ConversationParticipantContext> coworkers)
    {
        string value = NormalizeForComparison(topic);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] words = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1 && IsNameLikeToken(words[0]))
            return true;

        if (coworkers != null)
        {
            foreach (ConversationParticipantContext coworker in coworkers)
            {
                if (coworker == null || string.IsNullOrWhiteSpace(coworker.displayName))
                    continue;
                if (value == NormalizeForComparison(coworker.displayName))
                    return true;
            }
        }

        return false;
    }

    public static bool IsNameLikeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || value.Length > 20)
            return false;

        foreach (char c in value)
        {
            if (!char.IsLetter(c) && c != '\'' && c != '-')
                return false;
        }

        return true;
    }

    public static string DisplayName(AgentProfile profile, string fallback)
    {
        return profile != null && !string.IsNullOrWhiteSpace(profile.displayName)
            ? profile.displayName : fallback;
    }

    public static string JoinParticipantNames(List<ConversationParticipantContext> participants)
    {
        StringBuilder result = new();
        if (participants == null)
            return "";
        foreach (ConversationParticipantContext participant in participants)
        {
            if (participant == null || string.IsNullOrWhiteSpace(participant.displayName))
                continue;
            if (result.Length > 0)
                result.Append(", ");
            result.Append(participant.displayName.Trim());
        }
        return result.ToString();
    }

    public static string JoinActionTypes(List<OfficeActionType> actions)
    {
        StringBuilder result = new();
        foreach (OfficeActionType action in actions)
        {
            if (result.Length > 0)
                result.Append(", ");
            result.Append(action);
        }
        return result.ToString();
    }

    public static string BuildReplyKeyList(int count)
    {
        StringBuilder result = new();
        for (int i = 0; i < count; i++)
        {
            if (result.Length > 0)
                result.Append(", ");
            result.Append("reply").Append(i + 1);
        }
        return result.ToString();
    }

    public static string FormatSocialMemory(SocialMemoryEntry entry)
    {
        if (entry == null)
            return "<invalid>";
        string privacy = entry.isPrivate ? "private, " : "";
        string target = string.IsNullOrWhiteSpace(entry.targetAgent)
            ? "" : " -> " + entry.targetAgent.Trim();
        return "[" + privacy + entry.type + ", " + entry.status + "] " +
            entry.sourceAgent + target + ": " + entry.subject;
    }

    public static void AppendRecentMemory(StringBuilder target, AgentProfile profile, int maxLines,
        string ownerName = null)
    {
        if (profile == null || profile.memory.Count == 0)
            return;
        target.AppendLine(string.IsNullOrWhiteSpace(ownerName)
            ? "Your recent personal memories:"
            : ownerName.Trim() + "'s recent memories:");
        int start = Mathf.Max(0, profile.memory.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < profile.memory.Count; i++)
            target.Append("- ").Append(profile.memory[i]).AppendLine();
    }

    public static void AppendSocialMemory(StringBuilder target, AgentProfile profile, int maxLines,
        string ownerName = null)
    {
        if (profile == null || profile.socialMemory.Count == 0)
            return;
        target.AppendLine(string.IsNullOrWhiteSpace(ownerName)
            ? "Your social knowledge and commitments:"
            : ownerName.Trim() + "'s social knowledge and commitments:");
        int start = Mathf.Max(0, profile.socialMemory.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < profile.socialMemory.Count; i++)
            target.Append("- ").Append(FormatSocialMemory(profile.socialMemory[i])).AppendLine();
    }

    public static void AppendWorldEvents(StringBuilder target, List<string> worldEvents, int maxLines = 4)
    {
        if (worldEvents == null || worldEvents.Count == 0)
            return;
        target.AppendLine("Recent shared office events:");
        int start = Mathf.Max(0, worldEvents.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < worldEvents.Count; i++)
            target.Append("- ").Append(worldEvents[i]).AppendLine();
    }

    public static void AppendRecentList(StringBuilder target, string heading,
        List<string> values, int maxLines)
    {
        if (target == null || values == null || values.Count == 0)
            return;
        target.AppendLine(heading);
        int start = Mathf.Max(0, values.Count - Mathf.Max(1, maxLines));
        for (int i = start; i < values.Count; i++)
            target.Append("- ").Append(values[i]).AppendLine();
    }
}
