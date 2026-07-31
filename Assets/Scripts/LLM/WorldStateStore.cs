using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public sealed class WorldStateSnapshot
{
    public int version = 5;
    public string savedAtUtc;
    public double worldUnixSeconds;
    public List<PersistedAgentState> agents = new();
    public List<PersistedAgentRuntimeState> agentRuntime = new();
    public List<AgentRelationshipState> relationships = new();
    public List<OfficeEpisodeBeat> episodeReserve = new();
    public List<string> worldEvents = new();
    public List<PersistedOfficeStoryState> officeStories = new();
    public List<PersistedFurnitureState> furniture = new();
}

[Serializable]
public sealed class PersistedOfficeStoryState
{
    public string storyId;
    public int started;
    public int resolved;
}

[Serializable]
public sealed class PersistedFurnitureState
{
    public string socketId;
    public string eventId;
    public string spriteName;
    public float positionX;
    public float positionY;
    public float positionZ;
}

[Serializable]
public sealed class PersistedAgentState
{
    public string agentId;
    public List<string> memory = new();
    public List<SocialMemoryEntry> socialMemory = new();
    public List<string> recentTopics = new();
    public List<string> recentOpenings = new();
    public List<string> recentUtterances = new();
}

[Serializable]
public sealed class PersistedAgentRuntimeState
{
    public string agentId;
    public float positionX;
    public float positionY;
    public float energy;
    public float focus;
    public float social;
    public float productivity;
    public string lastAction;
    public string activeGoal;
    public float speedMultiplier;
    public float speedBuffRemainingSeconds;
    public float energyDecayMultiplier = 1f;
    public float focusDecayMultiplier = 1f;
    public float socialDecayMultiplier = 1f;
    public float decayOverrideRemainingSeconds;
    public long capturedAtUnixMilliseconds;
}

[Serializable]
public sealed class AgentRelationshipState
{
    public string firstAgentId;
    public string secondAgentId;
    public float affinity;
    public float trust;
    public float tension;
    public int interactions;
    public string lastEvent;
    public double lastInteractionWorldTime;
}

public static class WorldStateStore
{
    private const string FileName = "another-world-state.json";

    public static WorldStateSnapshot Load()
    {
        string path = GetPath();
        if (!File.Exists(path))
            return new WorldStateSnapshot();

        try
        {
            WorldStateSnapshot snapshot =
                JsonUtility.FromJson<WorldStateSnapshot>(File.ReadAllText(path));
            return snapshot ?? new WorldStateSnapshot();
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(WorldStateStore) + " could not load " + path +
                ": " + exception.Message);
            return new WorldStateSnapshot();
        }
    }

    public static void Save(IEnumerable<AgentProfile> profiles, List<string> worldEvents,
        double worldUnixSeconds, IEnumerable<PersistedAgentRuntimeState> agentRuntime,
        IEnumerable<AgentRelationshipState> relationships,
        IEnumerable<OfficeEpisodeBeat> episodeReserve,
        IEnumerable<PersistedOfficeStoryState> officeStories,
        IEnumerable<PersistedFurnitureState> furniture)
    {
        WorldStateSnapshot snapshot = new()
        {
            savedAtUtc = DateTime.UtcNow.ToString("O"),
            worldUnixSeconds = worldUnixSeconds,
            worldEvents = Copy(worldEvents),
            agentRuntime = CopyAgentRuntime(agentRuntime),
            relationships = CopyRelationships(relationships),
            episodeReserve = CopyEpisodeReserve(episodeReserve),
            officeStories = CopyOfficeStories(officeStories),
            furniture = CopyFurniture(furniture)
        };

        HashSet<string> savedAgentIds = new(StringComparer.OrdinalIgnoreCase);
        if (profiles != null)
        {
            foreach (AgentProfile profile in profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.agentId)
                    || !savedAgentIds.Add(profile.agentId))
                    continue;

                snapshot.agents.Add(new PersistedAgentState
                {
                    agentId = profile.agentId,
                    memory = Copy(profile.memory),
                    socialMemory = CopySocialMemory(profile.socialMemory),
                    recentTopics = Copy(profile.recentTopics),
                    recentOpenings = Copy(profile.recentOpenings),
                    recentUtterances = Copy(profile.recentUtterances)
                });
            }
        }

        try
        {
            string path = GetPath();
            string tempPath = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tempPath, JsonUtility.ToJson(snapshot, true));
            File.Copy(tempPath, path, true);
            File.Delete(tempPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning(nameof(WorldStateStore) + " could not save state: " +
                exception.Message);
        }
    }

    private static List<PersistedOfficeStoryState> CopyOfficeStories(
        IEnumerable<PersistedOfficeStoryState> source)
    {
        List<PersistedOfficeStoryState> result = new();
        if (source == null)
            return result;
        foreach (PersistedOfficeStoryState state in source)
            if (state != null && !string.IsNullOrWhiteSpace(state.storyId))
                result.Add(new PersistedOfficeStoryState
                {
                    storyId = state.storyId,
                    started = Mathf.Max(0, state.started),
                    resolved = Mathf.Max(0, state.resolved)
                });
        return result;
    }

    private static List<PersistedFurnitureState> CopyFurniture(
        IEnumerable<PersistedFurnitureState> source)
    {
        List<PersistedFurnitureState> result = new();
        if (source == null)
            return result;
        foreach (PersistedFurnitureState state in source)
            if (state != null && !string.IsNullOrWhiteSpace(state.socketId)
                && !string.IsNullOrWhiteSpace(state.eventId))
                result.Add(new PersistedFurnitureState
                {
                    socketId = state.socketId,
                    eventId = state.eventId,
                    spriteName = state.spriteName,
                    positionX = state.positionX,
                    positionY = state.positionY,
                    positionZ = state.positionZ
                });
        return result;
    }

    private static string GetPath()
    {
        return Path.Combine(Application.persistentDataPath, FileName);
    }

    private static List<string> Copy(List<string> source)
    {
        return source != null ? new List<string>(source) : new List<string>();
    }

    private static List<OfficeEpisodeBeat> CopyEpisodeReserve(
        IEnumerable<OfficeEpisodeBeat> source)
    {
        List<OfficeEpisodeBeat> result = new();
        if (source == null)
            return result;
        foreach (OfficeEpisodeBeat beat in source)
            if (beat != null)
                result.Add(beat);
        return result;
    }

    private static List<SocialMemoryEntry> CopySocialMemory(List<SocialMemoryEntry> source)
    {
        List<SocialMemoryEntry> result = new();
        if (source == null)
            return result;

        foreach (SocialMemoryEntry entry in source)
        {
            if (entry == null)
                continue;
            result.Add(new SocialMemoryEntry
            {
                type = entry.type,
                sourceAgent = entry.sourceAgent,
                targetAgent = entry.targetAgent,
                subject = entry.subject,
                isPrivate = entry.isPrivate,
                status = entry.status
            });
        }
        return result;
    }

    private static List<PersistedAgentRuntimeState> CopyAgentRuntime(
        IEnumerable<PersistedAgentRuntimeState> source)
    {
        List<PersistedAgentRuntimeState> result = new();
        if (source == null)
            return result;
        foreach (PersistedAgentRuntimeState state in source)
            if (state != null && !string.IsNullOrWhiteSpace(state.agentId))
                result.Add(state);
        return result;
    }

    private static List<AgentRelationshipState> CopyRelationships(
        IEnumerable<AgentRelationshipState> source)
    {
        List<AgentRelationshipState> result = new();
        if (source == null)
            return result;
        foreach (AgentRelationshipState state in source)
            if (state != null)
                result.Add(state);
        return result;
    }

}
