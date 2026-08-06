using System.Text.Json.Serialization;

public sealed record RegisterVisitorRequest(string? Alias, bool PublicAliasConsent);
public sealed record QrScanRequest(string ScanId);
public sealed record VisitorSession(string VisitorId, string Token, string DisplayName,
    bool PublicAliasConsent, int StoryContributions);
public sealed record VoteRequest(string OptionId);
public sealed record AppreciationRequest(string RecipientVisitorId, string Category,
    string CourierAgentId, bool PublicConsent);
public sealed record RewardIssueRequest(string SourceEventId, string RewardType,
    string DisplayName, string Description, int ClaimWindowSeconds);
public sealed record UnityEventRequest(string EventId, string Kind, string Title,
    string Detail, string[] AgentIds, long OccurredAtUnixMilliseconds);
public sealed record NewspaperRequest(string Date, string Headline, string Summary,
    string[] Stories, string Quote, string DecisionResult, string VisitorAcknowledgement,
    string TomorrowTeaser);

public sealed class DecisionRequest
{
    public string DecisionId { get; set; } = "";
    public string AuthorAgentId { get; set; } = "";
    public string AuthorDisplayName { get; set; } = "";
    public string Question { get; set; } = "";
    public int DurationSeconds { get; set; } = 180;
    public DecisionOptionRequest[] Options { get; set; } = [];
}

public sealed class DecisionOptionRequest
{
    public string OptionId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Reaction { get; set; } = "";
    public string Consequence { get; set; } = "";
}

public sealed class DecisionView
{
    public string DecisionId { get; set; } = "";
    public string AuthorAgentId { get; set; } = "";
    public string AuthorDisplayName { get; set; } = "";
    public string Question { get; set; } = "";
    public string Status { get; set; } = "";
    public long ClosesAtUnixMilliseconds { get; set; }
    public string WinningOptionId { get; set; } = "";
    public DecisionOptionView[] Options { get; set; } = [];
}

public sealed class DecisionOptionView
{
    public string OptionId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Reaction { get; set; } = "";
    public string Consequence { get; set; } = "";
    public int Votes { get; set; }
}

public sealed class HubEventView
{
    public long Cursor { get; set; }
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string VisitorDisplayName { get; set; } = "";
    public long CreatedAtUnixMilliseconds { get; set; }
}

public sealed class NewspaperView
{
    public string Date { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Summary { get; set; } = "";
    public string[] Stories { get; set; } = [];
    public string Quote { get; set; } = "";
    public string DecisionResult { get; set; } = "";
    public string VisitorAcknowledgement { get; set; } = "";
    public string TomorrowTeaser { get; set; } = "";
}

public sealed class RewardView
{
    public string RewardId { get; set; } = "";
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool PrototypeOnly { get; set; } = true;
    public long IssuedAtUnixMilliseconds { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public bool Claimed { get; set; }
    public bool Used { get; set; }
    public long UsedAtUnixMilliseconds { get; set; }
    public string Description { get; set; } = "";
}

public sealed class PublicStateView
{
    public DecisionView? ActiveDecision { get; set; }
    public string VisitorVoteOptionId { get; set; } = "";
    public NewspaperView? LatestNewspaper { get; set; }
    public VisitorSession? Visitor { get; set; }
    public RewardView[] Rewards { get; set; } = [];
    public RewardView? ClaimableReward { get; set; }
}

public sealed class UnityStateView
{
    public long Cursor { get; set; }
    public long LatestCursor { get; set; }
    public DecisionView? ActiveDecision { get; set; }
    public NewspaperView? LatestNewspaper { get; set; }
    public string NewspaperNeededDate { get; set; } = "";
    public HubEventView[] Events { get; set; } = [];
    public RewardView? ClaimableReward { get; set; }
}
