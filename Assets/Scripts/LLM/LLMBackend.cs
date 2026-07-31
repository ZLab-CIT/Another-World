using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

[System.Serializable]
public class ChatMessage
{
    public string role;
    public string content;

    public ChatMessage(string role, string content)
    {
        this.role = role;
        this.content = content;
    }
}

public class LLMOptions
{
    public string requestLabel = "Request";
    public float temperature = 0.8f;
    public int maxTokens = 256;
    public bool jsonMode = false;
    public bool highPriority = false;
    public int timeoutSeconds = 30;
    public int maxRetries = 2;
    public float retryBaseDelaySeconds = 2f;
    public string reasoningEffort;
    public bool excludeReasoning;
    public CancellationToken cancellationToken = CancellationToken.None;
}

public interface ILLMBackend
{
    bool IsLocal { get; }
    Task<string> CompleteAsync(List<ChatMessage> messages, LLMOptions options = null);
}

public class OpenAICompatibleBackend : ILLMBackend
{
    private sealed class TransportResponse
    {
        public long responseCode;
        public string body;
        public string error;
        public string retryAfter;
        public string remainingRequests;
        public string remainingTokens;
        public string requestReset;
        public string tokenReset;
        public UnityWebRequest.Result result;
    }

    private static readonly HttpClient desktopHttpClient = CreateDesktopHttpClient();
    private readonly string baseUrl;
    private readonly string apiKey;
    private readonly string model;
    private readonly SemaphoreSlim requestGate = new(2, 2);
    private DateTime cooldownUntilUtc = DateTime.MinValue;
    public bool IsLocal => IsLocalEndpoint();

    public OpenAICompatibleBackend(string baseUrl, string apiKey, string model)
    {
        this.baseUrl = (baseUrl ?? "").TrimEnd('/');
        this.apiKey = apiKey ?? "";
        this.model = model ?? "";
    }

    private static HttpClient CreateDesktopHttpClient()
    {
        IWebProxy systemProxy = WebRequest.GetSystemWebProxy();
        if (systemProxy != null)
            systemProxy.Credentials = CredentialCache.DefaultCredentials;

        HttpClientHandler handler = new()
        {
            UseProxy = systemProxy != null,
            Proxy = systemProxy,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
        };
        return new HttpClient(handler, true);
    }

    public async Task<string> CompleteAsync(List<ChatMessage> messages, LLMOptions options = null)
    {
        options ??= new LLMOptions();
        bool entered;
        try
        {
            int waitMilliseconds = options.highPriority ? 1500 : 150;
            entered = await requestGate.WaitAsync(waitMilliseconds,
                options.cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (!entered)
            return null;

        try
        {
            return await CompleteRequestAsync(messages, options);
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task<string> CompleteRequestAsync(List<ChatMessage> messages,
        LLMOptions options)
    {
        string logPrefix = "[LLM " + (string.IsNullOrWhiteSpace(options.requestLabel)
            ? "Request" : options.requestLabel.Trim()) + "] ";
        if (options.cancellationToken.IsCancellationRequested)
            return null;
        if (DateTime.UtcNow < cooldownUntilUtc)
            return null;

        RequestPayload payload = new()
        {
            model = model,
            messages = messages,
            temperature = Mathf.Round(options.temperature * 100f) / 100f,
            max_tokens = options.maxTokens,
            stream = false
        };

        string json = JsonUtility.ToJson(payload);
        json = ReplaceTemperatureJson(json, payload.temperature);
        if (IsGroqGptOssModel())
            json = json.Replace("\"max_tokens\":", "\"max_completion_tokens\":");
        if (ShouldOmitTemperature())
            json = RemoveJsonNumberField(json, "temperature");
        List<string> extraPayloadFields = new();
        if (options.jsonMode)
            extraPayloadFields.Add("\"response_format\":{\"type\":\"json_object\"}");
        if (!string.IsNullOrWhiteSpace(options.reasoningEffort))
            extraPayloadFields.Add("\"reasoning_effort\":\""
                + EscapeJsonString(options.reasoningEffort.Trim()) + "\"");
        if (options.excludeReasoning && IsGroqGptOssModel())
            extraPayloadFields.Add("\"include_reasoning\":false");
        if (IsGlmModel())
            extraPayloadFields.Add("\"thinking\":{\"type\":\"disabled\"}");

        if (extraPayloadFields.Count > 0)
            json = json.Substring(0, json.Length - 1) + "," +
                string.Join(",", extraPayloadFields) + "}";

        int maxAttempts = Mathf.Max(1, options.maxRetries + 1);
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            TransportResponse response = await SendAsync(json, options);
            long responseCode = response.responseCode;
            string responseBody = response.body;
            string requestError = response.error;
            string retryAfter = response.retryAfter;
            string remainingRequests = response.remainingRequests;
            string remainingTokens = response.remainingTokens;
            string requestReset = response.requestReset;
            string tokenReset = response.tokenReset;
            UnityWebRequest.Result result = response.result;

            if (result != UnityWebRequest.Result.Success)
            {
                if (options.cancellationToken.IsCancellationRequested)
                    return null;

                bool timedOut = IsRequestTimeout(responseCode, requestError);
                bool serviceOverloaded = IsServiceOverloaded(responseCode, responseBody);
                bool certificateFailure = IsCertificateFailure(requestError);
                bool permissionFailure = responseCode == 401 || responseCode == 403;
                bool jsonValidationFailure = options.jsonMode
                    && responseCode == 400
                    && !string.IsNullOrWhiteSpace(responseBody)
                    && (responseBody.Contains("json_validate_failed")
                        || responseBody.Contains("Failed to generate JSON")
                        || responseBody.Contains("Failed to validate JSON"));
                bool retryable = jsonValidationFailure
                    || !timedOut && !certificateFailure && !permissionFailure
                    && IsRetryable(responseCode, result);
                if (retryable && attempt + 1 < maxAttempts)
                {
                    float delaySeconds = GetRetryDelaySeconds(
                        retryAfter, options.retryBaseDelaySeconds, attempt);
                    if (serviceOverloaded)
                        delaySeconds = Mathf.Max(5f, delaySeconds);
                    cooldownUntilUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                    if (!await DelayAsync(delaySeconds, options.cancellationToken))
                        return null;
                    continue;
                }

                if (responseCode == 429)
                {
                    float minimumCooldown = serviceOverloaded ? 120f : 30f;
                    float cooldownSeconds = Mathf.Max(minimumCooldown,
                        GetRetryDelaySeconds(retryAfter, options.retryBaseDelaySeconds, attempt));
                    cooldownUntilUtc = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                }
                else if (timedOut)
                    cooldownUntilUtc = DateTime.UtcNow.AddSeconds(
                        IsLocalEndpoint() ? 3f : 10f);
                else if (certificateFailure)
                    cooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
                else if (permissionFailure)
                    cooldownUntilUtc = DateTime.UtcNow.AddMinutes(15);
                else if (responseCode <= 0)
                    cooldownUntilUtc = DateTime.UtcNow.AddSeconds(60);

                string failureMessage = logPrefix + nameof(OpenAICompatibleBackend) + " request failed (" +
                    responseCode + ", " + result + "): " + requestError +
                    (responseCode == 429 || timedOut
                        ? " Model-written scenes are temporarily paused." : "") +
                    (permissionFailure
                        ? " The key, project, or model permission was rejected; "
                            + "this provider is paused for 15 minutes." : "") +
                    FormatRateLimitStatus(remainingRequests, remainingTokens,
                        requestReset, tokenReset) +
                    FormatFailureBody(responseBody);
                if (serviceOverloaded)
                    Debug.Log(failureMessage);
                else
                    Debug.LogWarning(failureMessage);
                return null;
            }

            cooldownUntilUtc = DateTime.MinValue;
            ChatCompletionResponse resp = JsonUtility.FromJson<ChatCompletionResponse>(responseBody);
            if (resp == null || resp.choices == null || resp.choices.Length == 0)
            {
                Debug.LogWarning(logPrefix + nameof(OpenAICompatibleBackend) +
                    " returned an unreadable response: " + responseBody);
                return null;
            }

            string content = resp.choices[0].message != null ? resp.choices[0].message.content : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                Debug.LogWarning(logPrefix + nameof(OpenAICompatibleBackend) +
                    " returned empty message content: " + responseBody);
                return null;
            }

            if (resp.usage != null && resp.usage.total_tokens > 0)
            {
                Debug.Log(logPrefix + nameof(OpenAICompatibleBackend) + " token usage: prompt=" +
                    resp.usage.prompt_tokens + ", completion=" + resp.usage.completion_tokens +
                    ", total=" + resp.usage.total_tokens
                    + FormatRateLimitStatus(remainingRequests, remainingTokens,
                        requestReset, tokenReset));
            }

            return content;
        }

        return null;
    }

    private static string FormatFailureBody(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return "";
        if (responseBody.Contains("json_validate_failed")
            || responseBody.Contains("Failed to generate JSON")
            || responseBody.Contains("Failed to validate JSON"))
            return "\nProvider could not produce valid structured JSON.";
        const int limit = 600;
        string compact = responseBody.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return "\n" + (compact.Length <= limit
            ? compact : compact.Substring(0, limit) + "...");
    }

    private async Task<TransportResponse> SendAsync(string json, LLMOptions options)
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        return await SendWithDesktopHttpAsync(json, options);
#else
        return await SendWithUnityWebRequestAsync(json, options);
#endif
    }

    private async Task<TransportResponse> SendWithDesktopHttpAsync(
        string json, LLMOptions options)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                options.cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            options.timeoutSeconds > 0 ? options.timeoutSeconds : 30));
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post, baseUrl + "/chat/completions");
            request.Content = new StringContent(
                json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response =
                await desktopHttpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
            string body = response.Content != null
                ? await response.Content.ReadAsStringAsync()
                : "";
            return new TransportResponse
            {
                responseCode = (long)response.StatusCode,
                body = body,
                error = response.IsSuccessStatusCode
                    ? "" : response.ReasonPhrase,
                retryAfter = ReadHeader(response, "Retry-After"),
                remainingRequests =
                    ReadHeader(response, "x-ratelimit-remaining-requests"),
                remainingTokens =
                    ReadHeader(response, "x-ratelimit-remaining-tokens"),
                requestReset =
                    ReadHeader(response, "x-ratelimit-reset-requests"),
                tokenReset =
                    ReadHeader(response, "x-ratelimit-reset-tokens"),
                result = response.IsSuccessStatusCode
                    ? UnityWebRequest.Result.Success
                    : UnityWebRequest.Result.ProtocolError
            };
        }
        catch (OperationCanceledException)
        {
            return new TransportResponse
            {
                error = options.cancellationToken.IsCancellationRequested
                    ? "Request cancelled" : "Request timeout",
                result = UnityWebRequest.Result.ConnectionError
            };
        }
        catch (HttpRequestException exception)
        {
            return new TransportResponse
            {
                error = exception.Message,
                result = UnityWebRequest.Result.ConnectionError
            };
        }
    }

    private static string ReadHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string> values))
            return string.Join(",", values);
        if (response.Content != null
            && response.Content.Headers.TryGetValues(
                name, out IEnumerable<string> contentValues))
            return string.Join(",", contentValues);
        return null;
    }

    private async Task<TransportResponse> SendWithUnityWebRequestAsync(
        string json, LLMOptions options)
    {
        using UnityWebRequest request =
            new(baseUrl + "/chat/completions", "POST");
        request.uploadHandler =
            new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        if (!string.IsNullOrEmpty(apiKey))
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);
        request.timeout = options.timeoutSeconds > 0
            ? options.timeoutSeconds : 30;

        UnityWebRequest.Result result =
            await WebRequestTask(request, options.cancellationToken);
        return new TransportResponse
        {
            responseCode = request.responseCode,
            body = request.downloadHandler != null
                ? request.downloadHandler.text : "",
            error = request.error,
            retryAfter = request.GetResponseHeader("Retry-After"),
            remainingRequests =
                request.GetResponseHeader("x-ratelimit-remaining-requests"),
            remainingTokens =
                request.GetResponseHeader("x-ratelimit-remaining-tokens"),
            requestReset =
                request.GetResponseHeader("x-ratelimit-reset-requests"),
            tokenReset =
                request.GetResponseHeader("x-ratelimit-reset-tokens"),
            result = result
        };
    }

    private static bool IsRetryable(long responseCode, UnityWebRequest.Result result)
    {
        return responseCode == 408 || responseCode == 429 || responseCode >= 500
            || (responseCode <= 0 && result != UnityWebRequest.Result.Success);
    }

    private static bool IsRequestTimeout(long responseCode, string requestError)
    {
        return responseCode <= 0 && !string.IsNullOrWhiteSpace(requestError)
            && requestError.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCertificateFailure(string requestError)
    {
        if (string.IsNullOrWhiteSpace(requestError))
            return false;
        return requestError.IndexOf("certificate",
                   StringComparison.OrdinalIgnoreCase) >= 0
            || requestError.IndexOf("SSL",
                   StringComparison.OrdinalIgnoreCase) >= 0
            || requestError.IndexOf("authentication",
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsServiceOverloaded(long responseCode, string responseBody)
    {
        return responseCode == 429 && !string.IsNullOrWhiteSpace(responseBody)
            && (responseBody.IndexOf("\"code\":\"1305\"", StringComparison.OrdinalIgnoreCase) >= 0
                || responseBody.IndexOf("temporarily overloaded", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static float GetRetryDelaySeconds(string retryAfter, float baseDelaySeconds, int attempt)
    {
        if (int.TryParse(retryAfter, out int retryAfterSeconds) && retryAfterSeconds > 0)
            return Mathf.Clamp(retryAfterSeconds, 1, 60);

        float exponential = Mathf.Max(0.5f, baseDelaySeconds) * Mathf.Pow(2f, attempt);
        float jitter = (DateTime.UtcNow.Ticks % 500L) / 1000f;
        return Mathf.Min(30f, exponential + jitter);
    }

    private static string FormatRateLimitStatus(string remainingRequests,
        string remainingTokens, string requestReset, string tokenReset)
    {
        if (string.IsNullOrWhiteSpace(remainingRequests)
            && string.IsNullOrWhiteSpace(remainingTokens))
            return "";
        return " | provider remaining requests=" + (remainingRequests ?? "?")
            + " (reset " + (requestReset ?? "?") + "), tokens="
            + (remainingTokens ?? "?") + " (reset " + (tokenReset ?? "?") + ")";
    }

    private static async Task<bool> DelayAsync(float seconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Mathf.CeilToInt(seconds * 1000f), cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string ReplaceTemperatureJson(string json, float temperature)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        string formattedTemperature = temperature.ToString("0.00", CultureInfo.InvariantCulture);
        Regex temperaturePattern = new("\"temperature\"\\s*:\\s*[-0-9.Ee+]+");
        return temperaturePattern.Replace(json, "\"temperature\":" + formattedTemperature, 1);
    }

    private static string RemoveJsonNumberField(string json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(fieldName))
            return json;
        Regex fieldPattern = new(",?\"" + Regex.Escape(fieldName)
            + "\"\\s*:\\s*[-0-9.Ee+]+");
        string cleaned = fieldPattern.Replace(json, "", 1);
        return cleaned.Replace("{,", "{");
    }

    private static string EscapeJsonString(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static Task<UnityWebRequest.Result> WebRequestTask(UnityWebRequest req, CancellationToken cancellationToken)
    {
        return WaitForWebRequest(req, cancellationToken);
    }

    private bool IsGlmModel()
    {
        return model.StartsWith("glm-", StringComparison.OrdinalIgnoreCase)
            || baseUrl.IndexOf("z.ai", StringComparison.OrdinalIgnoreCase) >= 0
            || baseUrl.IndexOf("bigmodel", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool ShouldOmitTemperature()
    {
        return model.StartsWith("gemini-3.5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gemini-3.6", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsGroqGptOssModel()
    {
        return baseUrl.IndexOf("groq.com", StringComparison.OrdinalIgnoreCase) >= 0
            && model.StartsWith("openai/gpt-oss-", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsLocalEndpoint()
    {
        return baseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0
            || baseUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static async Task<UnityWebRequest.Result> WaitForWebRequest(
        UnityWebRequest req, CancellationToken cancellationToken)
    {
        UnityWebRequestAsyncOperation op = req.SendWebRequest();

        // Do not subscribe a managed delegate to AsyncOperation.completed here.
        // Unity can keep that native operation alive across an Editor domain reload,
        // then try to release a GC handle owned by the previous scripting domain.
        while (!op.isDone)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                req.Abort();
                break;
            }
            await Task.Yield();
        }

        return req.result;
    }

    [Serializable]
    private class RequestPayload
    {
        public string model;
        public List<ChatMessage> messages;
        public float temperature;
        public int max_tokens;
        public bool stream;
    }

    // JsonUtility assigns the response fields through reflection.
#pragma warning disable CS0649
    [Serializable]
    private class ChatCompletionResponse
    {
        public Choice[] choices;
        public Usage usage;
    }

    [Serializable]
    private class Choice
    {
        public ResponseMessage message;
    }

    [Serializable]
    private class ResponseMessage
    {
        public string role;
        public string content;
    }

    [Serializable]
    private class Usage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }
#pragma warning restore CS0649
}
