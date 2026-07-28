using System;
using System.Collections.Generic;
using System.Globalization;
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
    public CancellationToken cancellationToken = CancellationToken.None;
}

public interface ILLMBackend
{
    Task<string> CompleteAsync(List<ChatMessage> messages, LLMOptions options = null);
}

public class OpenAICompatibleBackend : ILLMBackend
{
    private readonly string baseUrl;
    private readonly string apiKey;
    private readonly string model;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTime cooldownUntilUtc = DateTime.MinValue;

    public OpenAICompatibleBackend(string baseUrl, string apiKey, string model)
    {
        this.baseUrl = (baseUrl ?? "").TrimEnd('/');
        this.apiKey = apiKey ?? "";
        this.model = model ?? "";
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
        List<string> extraPayloadFields = new();
        if (options.jsonMode)
            extraPayloadFields.Add("\"response_format\":{\"type\":\"json_object\"}");
        if (IsGlmModel())
            extraPayloadFields.Add("\"thinking\":{\"type\":\"disabled\"}");

        if (extraPayloadFields.Count > 0)
            json = json.Substring(0, json.Length - 1) + "," +
                string.Join(",", extraPayloadFields) + "}";

        int maxAttempts = Mathf.Max(1, options.maxRetries + 1);
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            long responseCode;
            string responseBody;
            string requestError;
            string retryAfter;
            string remainingRequests;
            string remainingTokens;
            string requestReset;
            string tokenReset;
            UnityWebRequest.Result result;

            using (UnityWebRequest req = new(baseUrl + "/chat/completions", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(apiKey))
                    req.SetRequestHeader("Authorization", "Bearer " + apiKey);
                req.timeout = options.timeoutSeconds > 0 ? options.timeoutSeconds : 30;

                result = await WebRequestTask(req, options.cancellationToken);
                responseCode = req.responseCode;
                responseBody = req.downloadHandler != null ? req.downloadHandler.text : "";
                requestError = req.error;
                retryAfter = req.GetResponseHeader("Retry-After");
                remainingRequests =
                    req.GetResponseHeader("x-ratelimit-remaining-requests");
                remainingTokens =
                    req.GetResponseHeader("x-ratelimit-remaining-tokens");
                requestReset = req.GetResponseHeader("x-ratelimit-reset-requests");
                tokenReset = req.GetResponseHeader("x-ratelimit-reset-tokens");
            }

            if (result != UnityWebRequest.Result.Success)
            {
                if (options.cancellationToken.IsCancellationRequested)
                    return null;

                bool timedOut = IsRequestTimeout(responseCode, requestError);
                bool serviceOverloaded = IsServiceOverloaded(responseCode, responseBody);
                bool retryable = !timedOut && IsRetryable(responseCode, result);
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
                    cooldownUntilUtc = DateTime.UtcNow.AddSeconds(30f);

                string failureMessage = logPrefix + nameof(OpenAICompatibleBackend) + " request failed (" +
                    responseCode + "): " + requestError +
                    (responseCode == 429 || timedOut
                        ? " Model-written scenes are temporarily paused." : "") +
                    FormatRateLimitStatus(remainingRequests, remainingTokens,
                        requestReset, tokenReset) +
                    (string.IsNullOrWhiteSpace(responseBody) ? "" : "\n" + responseBody);
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

    private static bool IsRetryable(long responseCode, UnityWebRequest.Result result)
    {
        return responseCode == 408 || responseCode == 429 || responseCode >= 500
            || (responseCode <= 0 && result == UnityWebRequest.Result.ConnectionError);
    }

    private static bool IsRequestTimeout(long responseCode, string requestError)
    {
        return responseCode <= 0 && !string.IsNullOrWhiteSpace(requestError)
            && requestError.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
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
