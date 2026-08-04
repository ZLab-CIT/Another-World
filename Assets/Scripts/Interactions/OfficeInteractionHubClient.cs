using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

#pragma warning disable CS0649

[DefaultExecutionOrder(-400)]
public sealed class OfficeInteractionHubClient : MonoBehaviour
{
    [Serializable]
    private sealed class UnityStateResponse
    {
        public long cursor;
        public long latestCursor;
        public HubDecision activeDecision;
        public OfficeNewspaper latestNewspaper;
        public string newspaperNeededDate;
        public HubEvent[] events;
        public HubReward claimableReward;
    }

    [Serializable]
    private sealed class HubDecision
    {
        public string decisionId;
        public string authorAgentId;
        public string authorDisplayName;
        public string question;
        public string status;
        public long closesAtUnixMilliseconds;
        public string winningOptionId;
        public HubDecisionOption[] options;
    }

    [Serializable]
    private sealed class HubDecisionOption
    {
        public string optionId;
        public string label;
        public string reaction;
        public string consequence;
        public int votes;
    }

    [Serializable]
    private sealed class HubEvent
    {
        public long cursor;
        public string eventId;
        public string kind;
        public string title;
        public string detail;
        public string agentId;
        public string visitorDisplayName;
        public long createdAtUnixMilliseconds;
    }

    [Serializable]
    private sealed class HubReward
    {
        public string rewardId;
        public string code;
        public string displayName;
        public string description;
        public long expiresAtUnixMilliseconds;
    }

    [Serializable]
    private sealed class RewardIssuePayload
    {
        public string sourceEventId;
        public string rewardType;
        public string displayName;
        public string description;
        public int claimWindowSeconds;
    }

    [Serializable]
    private sealed class UnityEventPayload
    {
        public string eventId;
        public string kind;
        public string title;
        public string detail;
        public string[] agentIds;
        public long occurredAtUnixMilliseconds;
    }

    [Serializable]
    private sealed class Outbox
    {
        public List<UnityEventPayload> events = new();
    }

    public static OfficeInteractionHubClient Instance { get; private set; }

    [Header("LAN Interaction Hub")]
    [SerializeField] private string baseUrl = "http://127.0.0.1:5074";
    [SerializeField, Min(0.5f)] private float pollSeconds = 1f;
    [SerializeField, Min(60f)] private float firstDecisionDelaySeconds = 90f;
    [SerializeField, Min(300f)] private float minimumDecisionIntervalSeconds = 1200f;
    [SerializeField, Min(300f)] private float maximumDecisionIntervalSeconds = 2400f;

    private readonly List<UnityEventPayload> outbox = new();
    private readonly List<HubEvent> recentHubEvents = new();
    private OfficeInteractionDisplay display;
    private OfficeAudienceDecision pendingDecision;
    private HubDecision activeDecision;
    private OfficeNewspaper latestNewspaper;
    private long eventCursor;
    private double nextDecisionUnixSeconds;
    private string unitySecret;
    private string publicUrl;
    private bool connected;
    private bool newspaperGenerationInFlight;

    public bool ShouldRequestDecision => connected && activeDecision == null
        && pendingDecision == null
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= nextDecisionUnixSeconds;

    public static OfficeInteractionHubClient Ensure()
    {
        if (Instance != null)
            return Instance;
        return new GameObject(nameof(OfficeInteractionHubClient))
            .AddComponent<OfficeInteractionHubClient>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        unitySecret = Environment.GetEnvironmentVariable("INTERACTION_HUB_SECRET") ?? "";
        string configuredUrl = Environment.GetEnvironmentVariable("INTERACTION_HUB_URL");
        if (!string.IsNullOrWhiteSpace(configuredUrl))
            baseUrl = configuredUrl;
        baseUrl = baseUrl.TrimEnd('/');
        publicUrl = ResolvePublicUrl();
        nextDecisionUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            + Mathf.Max(60f, firstDecisionDelaySeconds);
        eventCursor = Math.Max(0, long.TryParse(
            PlayerPrefs.GetString("InteractionHub.EventCursor", "0"),
            out long savedCursor) ? savedCursor : 0);
        LoadOutbox();
        display = OfficeInteractionDisplay.Ensure();
        display.SetConnection(false, publicUrl);
        StartCoroutine(PollLoop());
    }

    public void PublishDecision(OfficeAudienceDecision decision)
    {
        if (decision == null || decision.options == null || decision.options.Length < 2)
            return;
        pendingDecision = decision;
        StartCoroutine(PostDecision(decision));
    }

    public void RecordWorldEvent(string kind, string title, string detail,
        params string[] agentIds)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return;
        outbox.Add(new UnityEventPayload
        {
            eventId = "unity-" + Guid.NewGuid().ToString("N"),
            kind = string.IsNullOrWhiteSpace(kind) ? "world" : kind,
            title = title ?? "Office event",
            detail = detail,
            agentIds = agentIds ?? Array.Empty<string>(),
            occurredAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        while (outbox.Count > 128)
            outbox.RemoveAt(0);
        SaveOutbox();
    }

    public void PublishClaimableReward(string sourceEventId, string rewardType,
        string displayName, string description, int claimWindowSeconds = 300)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId)
            || string.IsNullOrWhiteSpace(displayName))
            return;
        StartCoroutine(PostReward(new RewardIssuePayload
        {
            sourceEventId = sourceEventId,
            rewardType = rewardType ?? "coupon",
            displayName = displayName,
            description = description ?? "",
            claimWindowSeconds = Mathf.Clamp(claimWindowSeconds, 60, 1800)
        }, Time.realtimeSinceStartup + Mathf.Clamp(claimWindowSeconds, 60, 1800)));
    }

    private IEnumerator PollLoop()
    {
        while (true)
        {
            yield return PollState();
            if (connected && outbox.Count > 0)
                yield return PostOutboxHead();
            if (connected && pendingDecision != null && activeDecision == null)
                yield return PostDecision(pendingDecision);
            yield return new WaitForSecondsRealtime(Mathf.Max(0.5f, pollSeconds));
        }
    }

    private IEnumerator PollState()
    {
        using UnityWebRequest request = UnityWebRequest.Get(
            baseUrl + "/api/unity/state?after=" + eventCursor);
        AddUnityHeaders(request);
        request.timeout = 4;
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            SetConnected(false);
            yield break;
        }

        UnityStateResponse state = null;
        try { state = JsonUtility.FromJson<UnityStateResponse>(request.downloadHandler.text); }
        catch { }
        if (state == null)
        {
            SetConnected(false);
            yield break;
        }
        SetConnected(true);
        if (state.latestCursor < eventCursor)
        {
            eventCursor = 0;
            PlayerPrefs.SetString("InteractionHub.EventCursor", "0");
            yield break;
        }
        eventCursor = Math.Max(eventCursor, state.cursor);
        PlayerPrefs.SetString("InteractionHub.EventCursor", eventCursor.ToString());
        activeDecision = state.activeDecision;
        latestNewspaper = state.latestNewspaper;
        if (state.claimableReward != null)
            display.SetReward(state.claimableReward.displayName,
                state.claimableReward.description,
                state.claimableReward.expiresAtUnixMilliseconds);
        else
            display.SetDecision(activeDecision?.authorDisplayName,
                activeDecision?.question, publicUrl, activeDecision != null);
        display.SetNewspaper(latestNewspaper);
        ProcessEvents(state.events);
        if (!newspaperGenerationInFlight
            && !string.IsNullOrWhiteSpace(state.newspaperNeededDate))
            StartCoroutine(GenerateNewspaper(state.newspaperNeededDate));
    }

    private void ProcessEvents(HubEvent[] events)
    {
        if (events == null)
            return;
        foreach (HubEvent item in events)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.eventId))
                continue;
            recentHubEvents.Add(item);
            while (recentHubEvents.Count > 60)
                recentHubEvents.RemoveAt(0);
            AIWorkerAgent agent = FindWorker(item.agentId);
            switch (item.kind)
            {
                case "vote_cast":
                    agent?.ReactToWorldEvent(item.detail);
                    if (agent != null && !string.IsNullOrWhiteSpace(
                            item.visitorDisplayName))
                        LLMBrainService.Instance?.Remember(agent.AgentId,
                            item.visitorDisplayName + " answered an office question: "
                            + item.detail);
                    break;
                case "decision_resolved":
                    LLMBrainService.Instance?.RememberWorldEvent(
                        "Office visitors decided: " + item.detail);
                    agent?.ReactToWorldEvent(item.detail);
                    VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
                        "Visitors decided", item.detail, null, 5f);
                    activeDecision = null;
                    nextDecisionUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        + UnityEngine.Random.Range(
                            Mathf.Max(300f, minimumDecisionIntervalSeconds),
                            Mathf.Max(minimumDecisionIntervalSeconds,
                                maximumDecisionIntervalSeconds));
                    break;
                case "appreciation":
                    if (agent != null)
                        agent.ShowThought(item.detail);
                    break;
                case "reward_issued":
                    agent?.ReactToWorldEvent(
                        "I prepared a surprise for a returning visitor.");
                    VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
                        item.title, item.detail, null, 5f);
                    break;
                case "reward_claimed":
                    VendingEventDispatcher.Instance?.ShowWorldAnnouncement(
                        "Coupon claimed", "The five-minute offer found its visitor.", null, 4f);
                    break;
            }
        }
    }

    private IEnumerator PostReward(RewardIssuePayload payload, float retryUntil)
    {
        while (Time.realtimeSinceStartup < retryUntil)
        {
            using UnityWebRequest request = MakeJsonRequest(
                baseUrl + "/api/unity/rewards", "POST", JsonUtility.ToJson(payload));
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                HubReward reward = JsonUtility.FromJson<HubReward>(
                    request.downloadHandler.text);
                if (reward != null)
                    display.SetReward(reward.displayName, reward.description,
                        reward.expiresAtUnixMilliseconds);
                yield break;
            }
            if (request.responseCode >= 400 && request.responseCode < 500)
                yield break;
            yield return new WaitForSecondsRealtime(5f);
        }
    }

    private IEnumerator PostDecision(OfficeAudienceDecision decision)
    {
        if (decision == null)
            yield break;
        string json = JsonUtility.ToJson(decision);
        using UnityWebRequest request = MakeJsonRequest(
            baseUrl + "/api/unity/decisions", "POST", json);
        yield return request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
        {
            pendingDecision = null;
            activeDecision = JsonUtility.FromJson<HubDecision>(request.downloadHandler.text);
            display.SetDecision(activeDecision?.authorDisplayName,
                activeDecision?.question, publicUrl, true);
        }
        else if (request.responseCode == 409)
        {
            pendingDecision = null;
        }
    }

    private IEnumerator PostOutboxHead()
    {
        UnityEventPayload payload = outbox[0];
        using UnityWebRequest request = MakeJsonRequest(
            baseUrl + "/api/unity/events", "POST", JsonUtility.ToJson(payload));
        yield return request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
        {
            outbox.RemoveAt(0);
            SaveOutbox();
        }
    }

    private IEnumerator GenerateNewspaper(string date)
    {
        newspaperGenerationInFlight = true;
        string context = string.Join(" | ", recentHubEvents
            .TakeLast(20).Select(item => item.title + ": " + item.detail));
        var task = LLMBrainService.Instance != null
            ? LLMBrainService.Instance.GenerateDailyNewspaperAsync(date, context)
            : null;
        if (task != null)
            while (!task.IsCompleted)
                yield return null;
        OfficeNewspaper newspaper = task != null && task.Status ==
            System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : null;
        newspaper ??= OfficeNewspaper.CreateFallback(date, recentHubEvents
            .Select(item => item.detail).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        using UnityWebRequest request = MakeJsonRequest(
            baseUrl + "/api/unity/newspapers", "POST", JsonUtility.ToJson(newspaper));
        yield return request.SendWebRequest();
        newspaperGenerationInFlight = false;
    }

    private UnityWebRequest MakeJsonRequest(string url, string method, string json)
    {
        UnityWebRequest request = new(url, method);
        request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        AddUnityHeaders(request);
        request.timeout = 6;
        return request;
    }

    private void AddUnityHeaders(UnityWebRequest request)
    {
        if (!string.IsNullOrWhiteSpace(unitySecret))
            request.SetRequestHeader("X-Unity-Secret", unitySecret);
    }

    private void SetConnected(bool value)
    {
        if (connected == value)
            return;
        connected = value;
        display.SetConnection(value, publicUrl);
    }

    private static AIWorkerAgent FindWorker(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;
        foreach (AIWorkerAgent worker in FindObjectsOfType<AIWorkerAgent>())
            if (worker != null && string.Equals(worker.AgentId, agentId,
                    StringComparison.OrdinalIgnoreCase))
                return worker;
        return null;
    }

    private string ResolvePublicUrl()
    {
        string configured = Environment.GetEnvironmentVariable(
            "INTERACTION_HUB_PUBLIC_URL");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/') + "/";
        try
        {
            Uri uri = new(baseUrl);
            IPAddress address = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(value));
            if (address != null)
                return uri.Scheme + "://" + address + ":" + uri.Port + "/";
        }
        catch { }
        return baseUrl + "/";
    }

    private string OutboxPath => Path.Combine(Application.persistentDataPath,
        "interaction-hub-outbox.json");

    private void LoadOutbox()
    {
        try
        {
            if (!File.Exists(OutboxPath))
                return;
            Outbox saved = JsonUtility.FromJson<Outbox>(File.ReadAllText(OutboxPath));
            if (saved?.events != null)
                outbox.AddRange(saved.events.Where(item => item != null));
        }
        catch { }
    }

    private void SaveOutbox()
    {
        try { File.WriteAllText(OutboxPath, JsonUtility.ToJson(new Outbox { events = outbox }, true)); }
        catch (Exception exception) { Debug.LogWarning("[Interaction hub] Could not save outbox: " + exception.Message); }
    }
}

[Serializable]
public sealed class OfficeNewspaper
{
    public string date;
    public string headline;
    public string summary;
    public string[] stories;
    public string quote;
    public string decisionResult;
    public string visitorAcknowledgement;
    public string tomorrowTeaser;

    public static OfficeNewspaper CreateFallback(string date, string[] events)
    {
        string[] useful = events?.Where(value => !string.IsNullOrWhiteSpace(value))
            .TakeLast(3).ToArray() ?? Array.Empty<string>();
        if (useful.Length == 0)
            useful = new[] { "The office carried on quietly, leaving room for tomorrow's surprises." };
        return new OfficeNewspaper
        {
            date = date,
            headline = "Small Changes in Another World",
            summary = "The office remembered what its workers and visitors changed today.",
            stories = useful,
            quote = "Tomorrow should not have to repeat today.",
            decisionResult = "Visitor decisions will appear here after a vote resolves.",
            visitorAcknowledgement = "Thanks to everyone who looked in on the office.",
            tomorrowTeaser = "A new question is waiting beyond the next workday."
        };
    }
}

public sealed class OfficeInteractionDisplay : MonoBehaviour
{
    private CanvasGroup group;
    private Canvas canvas;
    private RectTransform panelRect;
    private RawImage qrImage;
    private TMP_Text eyebrow;
    private TMP_Text title;
    private TMP_Text body;
    private TMP_Text combinedText;
    private string currentQrUrl;
    private OfficeNewspaper newspaper;
    private float nextPaperTime;
    private bool decisionVisible;
    private OfficeInteractionDisplaySettings settings;
    private float nextLayoutRefreshTime;
    private bool usesAuthoredPrefab;

    public static OfficeInteractionDisplay Ensure()
    {
        OfficeInteractionDisplay existing = FindFirstObjectByType<OfficeInteractionDisplay>();
        if (existing != null)
            return existing;
        return new GameObject(nameof(OfficeInteractionDisplay)).AddComponent<OfficeInteractionDisplay>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        gameObject.AddComponent<GraphicRaycaster>();
        group = gameObject.AddComponent<CanvasGroup>();

        settings = Resources.Load<OfficeInteractionDisplaySettings>(
            "InteractionHub/DisplaySettings");
        if (!TryCreatePrefabView())
            CreateFallbackView();
        ApplyLayout();
        nextPaperTime = Time.unscaledTime + 90f;
    }

    private bool TryCreatePrefabView()
    {
        if (settings == null || settings.panelPrefab == null)
            return false;

        OfficeInteractionDisplayView view = Instantiate(settings.panelPrefab, transform, false);
        if (!view.IsConfigured)
        {
            Debug.LogError("[Interaction hub] QR tile prefab is missing one or more view references. Using the generated tile.", view);
            Destroy(view.gameObject);
            return false;
        }

        panelRect = view.Panel;
        qrImage = view.QrImage;
        combinedText = view.CombinedText;
        eyebrow = view.Eyebrow;
        title = view.Title;
        body = view.Body;
        usesAuthoredPrefab = true;
        return true;
    }

    private void CreateFallbackView()
    {
        GameObject panel = new("Interaction panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(transform, false);
        panelRect = panel.GetComponent<RectTransform>();
        panel.GetComponent<Image>().color = new Color(0.96f, 0.91f, 0.79f, 0.97f);

        qrImage = CreateQr(panel.transform);
        eyebrow = CreateText("Eyebrow", panel.transform, new Vector2(144f, -18f), new Vector2(302f, 24f),
            14, FontStyles.Bold, new Color(0.76f, 0.2f, 0.11f));
        title = CreateText("Title", panel.transform, new Vector2(144f, -43f), new Vector2(302f, 48f),
            25, FontStyles.Bold, new Color(0.07f, 0.11f, 0.13f));
        body = CreateText("Body", panel.transform, new Vector2(144f, -93f), new Vector2(302f, 52f),
            15, FontStyles.Normal, new Color(0.12f, 0.18f, 0.2f));
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextLayoutRefreshTime)
        {
            nextLayoutRefreshTime = Time.unscaledTime + 1f;
            ApplyLayout();
        }
        if (!decisionVisible && newspaper != null && Time.unscaledTime >= nextPaperTime)
        {
            SetCopy("TODAY'S TERRARIUM POST", newspaper.headline, newspaper.summary);
            nextPaperTime = Time.unscaledTime + 120f;
        }
    }

    private void ApplyLayout()
    {
        if (panelRect == null)
            return;
        InteractionDisplayCorner corner = settings != null
            ? settings.corner : InteractionDisplayCorner.TopLeft;
        Vector2 anchor = corner switch
        {
            InteractionDisplayCorner.TopRight => new Vector2(1f, 1f),
            InteractionDisplayCorner.BottomLeft => new Vector2(0f, 0f),
            InteractionDisplayCorner.BottomRight => new Vector2(1f, 0f),
            _ => new Vector2(0f, 1f)
        };
        Vector2 margin = settings != null ? settings.margin : new Vector2(24f, 24f);
        panelRect.anchorMin = anchor;
        panelRect.anchorMax = anchor;
        panelRect.pivot = anchor;
        panelRect.anchoredPosition = new Vector2(
            anchor.x > 0.5f ? -margin.x : margin.x,
            anchor.y > 0.5f ? -margin.y : margin.y);
        panelRect.localScale = Vector3.one * (settings != null ? settings.scale : 1f);
        canvas.sortingOrder = settings != null ? settings.canvasSortingOrder : 900;
        if (usesAuthoredPrefab)
            return;

        panelRect.sizeDelta = settings != null
            ? settings.panelSize : new Vector2(470f, 158f);
        eyebrow.fontSize = settings != null ? settings.eyebrowFontSize : 14f;
        title.fontSize = settings != null ? settings.titleFontSize : 25f;
        body.fontSize = settings != null ? settings.bodyFontSize : 15f;
        if (qrImage != null)
        {
            qrImage.rectTransform.sizeDelta = settings != null
                ? settings.qrSize : new Vector2(124f, 124f);
            float textX = 22f + qrImage.rectTransform.sizeDelta.x;
            float textWidth = Mathf.Max(120f, panelRect.sizeDelta.x - textX - 18f);
            eyebrow.rectTransform.anchoredPosition = new Vector2(textX, -18f);
            eyebrow.rectTransform.sizeDelta = new Vector2(textWidth, 24f);
            title.rectTransform.anchoredPosition = new Vector2(textX, -43f);
            title.rectTransform.sizeDelta = new Vector2(textWidth, 48f);
            body.rectTransform.anchoredPosition = new Vector2(textX, -93f);
            body.rectTransform.sizeDelta = new Vector2(textWidth,
                Mathf.Max(30f, panelRect.sizeDelta.y - 106f));
        }
    }

    public void SetConnection(bool connected, string url)
    {
        SetCopy(
            connected ? "SCAN TO INFLUENCE THE OFFICE" : "INTERACTION HUB OFFLINE",
            connected ? "Another World is listening" : "The world continues locally",
            connected ? url : "Start the LAN hub to enable visitor decisions.");
        if (connected && currentQrUrl != url)
        {
            currentQrUrl = url;
            StartCoroutine(LoadQr(url));
        }
    }

    public void SetDecision(string author, string question, string url, bool visible)
    {
        decisionVisible = visible;
        if (!visible)
        {
            SetCopy("SCAN TO INFLUENCE THE OFFICE", "Another World is listening", url);
            return;
        }
        SetCopy(
            (author ?? "A CHARACTER").ToUpperInvariant() + " ASKS THE OFFICE",
            question ?? "A visitor decision is open",
            "Scan to vote. The majority changes the next story.");
    }

    public void SetReward(string rewardName, string description,
        long expiresAtUnixMilliseconds)
    {
        long remainingMilliseconds = Math.Max(0L, expiresAtUnixMilliseconds
            - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        int minutes = Mathf.Max(1, Mathf.CeilToInt(remainingMilliseconds / 60000f));
        SetCopy("COUPON AVAILABLE - SCAN TO CLAIM",
            rewardName ?? "Office reward",
            (description ?? "A character prepared a reward.") + " First claim wins; "
            + minutes + (minutes == 1 ? " minute remains." : " minutes remain."));
    }

    private void SetCopy(string eyebrowValue, string titleValue, string bodyValue)
    {
        if (combinedText != null)
        {
            combinedText.text = $"<b>{eyebrowValue}</b>\n{titleValue}\n{bodyValue}";
            return;
        }

        eyebrow.text = eyebrowValue;
        title.text = titleValue;
        body.text = bodyValue;
    }

    public void SetNewspaper(OfficeNewspaper value)
    {
        newspaper = value;
    }

    private IEnumerator LoadQr(string target)
    {
        string hub = Environment.GetEnvironmentVariable("INTERACTION_HUB_URL")
            ?? "http://127.0.0.1:5074";
        using UnityWebRequest request = UnityWebRequestTexture.GetTexture(
            hub.TrimEnd('/') + "/api/qr?url=" + UnityWebRequest.EscapeURL(target));
        request.timeout = 5;
        yield return request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
            qrImage.texture = DownloadHandlerTexture.GetContent(request);
    }

    private static RawImage CreateQr(Transform parent)
    {
        GameObject go = new("QR", typeof(RectTransform), typeof(RawImage));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(14f, -14f);
        rect.sizeDelta = new Vector2(124f, 124f);
        return go.GetComponent<RawImage>();
    }

    private static TMP_Text CreateText(string name, Transform parent, Vector2 position,
        Vector2 size, float fontSize, FontStyles style, Color color)
    {
        GameObject go = new(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        TMP_Text text = go.GetComponent<TMP_Text>();
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = color;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }
}
#pragma warning restore CS0649
