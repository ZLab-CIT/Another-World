using QRCoder;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<HubStore>();
builder.Services.AddHostedService<DecisionResolutionWorker>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

bool IsUnityAuthorized(HttpRequest request)
{
    string configured = Environment.GetEnvironmentVariable("INTERACTION_HUB_SECRET") ?? "";
    if (string.IsNullOrWhiteSpace(configured))
        return request.HttpContext.Connection.RemoteIpAddress is { } address
            && System.Net.IPAddress.IsLoopback(address);
    return request.Headers.TryGetValue("X-Unity-Secret", out var supplied)
        && string.Equals(supplied.ToString(), configured, StringComparison.Ordinal);
}

string? VisitorToken(HttpRequest request) =>
    request.Headers.TryGetValue("X-Visitor-Token", out var token) ? token.ToString() : null;

app.MapPost("/api/visitors/register", (HttpRequest http, RegisterVisitorRequest request, HubStore store) =>
    Results.Ok(store.Register(VisitorToken(http), request)));

app.MapPost("/api/visits/scan", (HttpRequest http, QrScanRequest request, HubStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.ScanId)) return Results.BadRequest();
    return store.RecordQrScan(VisitorToken(http), request.ScanId)
        ? Results.Accepted() : Results.Ok();
});

app.MapGet("/api/state", (HttpRequest http, HubStore store) =>
{
    VisitorSession? visitor = store.GetVisitor(VisitorToken(http));
    DecisionView? activeDecision = store.GetActiveDecision();
    return Results.Ok(new PublicStateView
    {
        ActiveDecision = activeDecision,
        VisitorVoteOptionId = visitor != null && activeDecision != null
            ? store.GetVisitorVote(activeDecision.DecisionId, visitor.VisitorId) : "",
        LatestNewspaper = store.GetLatestNewspaper(),
        Visitor = visitor,
        Rewards = visitor == null ? [] : store.GetRewards(visitor.VisitorId),
        ClaimableReward = store.GetActiveClaimableReward()
    });
});

app.MapPost("/api/decisions/{decisionId}/votes", (string decisionId, HttpRequest http,
    VoteRequest request, HubStore store) =>
{
    string? token = VisitorToken(http);
    if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
    DecisionView? result = store.CastVote(token, decisionId, request);
    return result == null ? Results.BadRequest() : Results.Ok(result);
});

app.MapPost("/api/appreciation", (HttpRequest http, AppreciationRequest request, HubStore store) =>
{
    string? token = VisitorToken(http);
    if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
    return store.AddAppreciation(token, request) ? Results.Accepted() : Results.BadRequest();
});

app.MapGet("/api/visitors/public", (HubStore store) => Results.Ok(
    store.GetPublicVisitors().Select(value => new { visitorId = value.id, displayName = value.name })));

app.MapPost("/api/rewards/{code}/claim", (string code, HttpRequest http, HubStore store) =>
{
    string? token = VisitorToken(http);
    if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
    return store.ClaimReward(token, code) ? Results.Accepted() : Results.Conflict();
});

app.MapPost("/api/rewards/{code}/use", (string code, HttpRequest http, HubStore store) =>
{
    string? token = VisitorToken(http);
    if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
    return store.UseReward(token, code) ? Results.Accepted() : Results.Conflict();
});

app.MapGet("/api/newspapers", (HubStore store) => Results.Ok(store.GetNewspaperArchive()));

app.MapGet("/rewards/{code}", (string code, HubStore store) =>
{
    RewardView? reward = store.GetRewardByCode(code);
    if (reward == null) return Results.NotFound();
    string safeCode = System.Net.WebUtility.HtmlEncode(reward.Code);
    string safeName = System.Net.WebUtility.HtmlEncode(reward.DisplayName);
    return Results.Content($$"""
        <!doctype html><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{safeName}}</title><style>body{background:#f6efda;color:#141d23;font-family:Georgia,serif;display:grid;place-items:center;min-height:100vh;margin:0}main{border:3px solid;padding:36px;box-shadow:8px 8px #141d23;text-align:center;background:#ffd764}code{font:900 28px monospace}strong{display:block;margin-top:22px;color:#a32d1d}</style>
        <main><small>ANOTHER WORLD OFFICE</small><h1>{{safeName}}</h1><code>{{safeCode}}</code><strong>PROTOTYPE ONLY, NOT REDEEMABLE</strong></main>
        """, "text/html");
});

app.MapGet("/api/events", async (HttpContext context, HubStore store) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    long cursor = long.TryParse(context.Request.Query["after"], out long parsed) ? parsed : 0;
    while (!context.RequestAborted.IsCancellationRequested)
    {
        HubEventView[] events = store.GetEvents(cursor, 50);
        foreach (HubEventView item in events)
        {
            await context.Response.WriteAsync("event: hub\n", context.RequestAborted);
            await context.Response.WriteAsync("data: " + System.Text.Json.JsonSerializer.Serialize(item) + "\n\n",
                context.RequestAborted);
            cursor = Math.Max(cursor, item.Cursor);
        }
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await Task.Delay(1500, context.RequestAborted);
    }
});

app.MapGet("/api/qr", (HttpRequest request) =>
{
    string target = request.Query["url"].ToString();
    if (string.IsNullOrWhiteSpace(target)) target = $"{request.Scheme}://{request.Host}/";
    using QRCodeGenerator generator = new();
    using QRCodeData data = generator.CreateQrCode(target, QRCodeGenerator.ECCLevel.Q);
    using PngByteQRCode code = new(data);
    return Results.File(code.GetGraphic(8, new byte[] { 20, 29, 35 }, new byte[] { 246, 239, 218 }), "image/png");
});

app.MapGet("/api/unity/state", (long? after, HttpRequest http, HubStore store) =>
{
    if (!IsUnityAuthorized(http)) return Results.Unauthorized();
    HubEventView[] events = store.GetEvents(after ?? 0, 100);
    return Results.Ok(new UnityStateView
    {
        Cursor = events.Length == 0 ? after ?? 0 : events[^1].Cursor,
        LatestCursor = store.GetLatestCursor(),
        ActiveDecision = store.GetActiveDecision(),
        LatestNewspaper = store.GetLatestNewspaper(),
        NewspaperNeededDate = store.NewspaperNeededDate(),
        Events = events,
        ClaimableReward = store.GetActiveClaimableReward()
    });
});

app.MapPost("/api/unity/decisions", (HttpRequest http, DecisionRequest request, HubStore store) =>
{
    if (!IsUnityAuthorized(http)) return Results.Unauthorized();
    if (!DecisionValidator.IsValid(request, out string error)) return Results.BadRequest(error);
    DecisionView? created = store.CreateDecision(request);
    return created == null ? Results.Conflict("A decision is already active.") : Results.Ok(created);
});

app.MapPost("/api/unity/events", (HttpRequest http, UnityEventRequest request, HubStore store) =>
{
    if (!IsUnityAuthorized(http)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.Detail))
        return Results.BadRequest();
    return store.AddUnityEvent(request) ? Results.Accepted() : Results.Ok();
});

app.MapPost("/api/unity/rewards", (HttpRequest http, RewardIssueRequest request, HubStore store) =>
{
    if (!IsUnityAuthorized(http)) return Results.Unauthorized();
    RewardView? reward = store.IssueClaimableReward(request);
    return reward == null ? Results.BadRequest() : Results.Ok(reward);
});

app.MapPost("/api/unity/newspapers", (HttpRequest http, NewspaperRequest request, HubStore store) =>
{
    if (!IsUnityAuthorized(http)) return Results.Unauthorized();
    if (!DateOnly.TryParse(request.Date, out _) || string.IsNullOrWhiteSpace(request.Headline))
        return Results.BadRequest();
    store.SaveNewspaper(request);
    return Results.Accepted();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", time = DateTimeOffset.UtcNow }));
app.Run();

public static class DecisionValidator
{
    public static bool IsValid(DecisionRequest request, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(request.DecisionId) || request.DecisionId.Length > 80
            || string.IsNullOrWhiteSpace(request.AuthorAgentId) || request.AuthorAgentId.Length > 40
            || string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 180)
        { error = "Invalid decision identity or question."; return false; }
        if (request.Options == null || request.Options.Length is < 2 or > 3)
        { error = "A decision requires two or three options."; return false; }
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (DecisionOptionRequest option in request.Options)
        {
            if (string.IsNullOrWhiteSpace(option.OptionId) || !ids.Add(option.OptionId)
                || string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 72
                || string.IsNullOrWhiteSpace(option.Reaction) || option.Reaction.Length > 180
                || string.IsNullOrWhiteSpace(option.Consequence) || option.Consequence.Length > 220)
            { error = "Decision options are incomplete, duplicated, or too long."; return false; }
        }
        return true;
    }
}

public sealed class DecisionResolutionWorker(HubStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { store.ResolveExpiredDecision(); }
            catch (Exception exception) { Console.Error.WriteLine("Decision resolution failed: " + exception.Message); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
