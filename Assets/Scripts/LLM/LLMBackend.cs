using System;
using System.Collections.Generic;
using System.Text;
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
    public float temperature = 0.8f;
    public int maxTokens = 256;
    public bool jsonMode = false;
    public LLMJsonSchema structuredSchema = LLMJsonSchema.None;
    public int timeoutSeconds = 30;
    public CancellationToken cancellationToken = CancellationToken.None;
}

public enum LLMJsonSchema
{
    None,
    ConversationPlan,
    ConversationScript
}

public interface ILLMBackend
{
    bool IsAvailable { get; }

    Task<string> CompleteAsync(List<ChatMessage> messages, LLMOptions options = null);
}

public class OpenAICompatibleBackend : ILLMBackend
{
    private readonly string baseUrl;
    private readonly string apiKey;
    private readonly string model;

    public bool IsAvailable => true;

    public OpenAICompatibleBackend(string baseUrl, string apiKey, string model)
    {
        this.baseUrl = (baseUrl ?? "").TrimEnd('/');
        this.apiKey = apiKey ?? "";
        this.model = model ?? "";
    }

    public async Task<string> CompleteAsync(List<ChatMessage> messages, LLMOptions options = null)
    {
        options ??= new LLMOptions();
        if (options.cancellationToken.IsCancellationRequested)
            return null;

        RequestPayload payload = new()
        {
            model = model,
            messages = messages,
            temperature = options.temperature,
            max_tokens = options.maxTokens,
            stream = false
        };

        string json = JsonUtility.ToJson(payload);
        if (options.jsonMode)
        {
            string responseFormat = BuildResponseFormat(options.structuredSchema);
            json = json.Substring(0, json.Length - 1) +
                ",\"response_format\":" + responseFormat + "}";
        }

        using (UnityWebRequest req = new(baseUrl + "/chat/completions", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(apiKey))
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);
            req.timeout = (options != null && options.timeoutSeconds > 0) ? options.timeoutSeconds : 30;

            UnityWebRequest.Result result = await WebRequestTask(req, options.cancellationToken);

            if (result != UnityWebRequest.Result.Success)
            {
                if (options.cancellationToken.IsCancellationRequested)
                    return null;
                Debug.LogWarning(nameof(OpenAICompatibleBackend) + " request failed: " + req.error);
                return null;
            }

            ChatCompletionResponse resp = JsonUtility.FromJson<ChatCompletionResponse>(req.downloadHandler.text);
            if (resp == null || resp.choices == null || resp.choices.Length == 0)
                return null;

            return resp.choices[0].message.content;
        }
    }

    private static Task<UnityWebRequest.Result> WebRequestTask(UnityWebRequest req, CancellationToken cancellationToken)
    {
        return WaitForWebRequest(req, cancellationToken);
    }

    private static string BuildResponseFormat(LLMJsonSchema schema)
    {
        switch (schema)
        {
            case LLMJsonSchema.ConversationPlan:
                return "{\"type\":\"json_schema\",\"json_schema\":{" +
                    "\"name\":\"conversation_plan\",\"strict\":true,\"schema\":{" +
                    "\"type\":\"object\",\"properties\":{" +
                    "\"targetAgent\":{\"type\":\"string\"}," +
                    "\"topic\":{\"type\":\"string\"}," +
                    "\"openingLine\":{\"type\":\"string\"}}," +
                    "\"required\":[\"targetAgent\",\"topic\",\"openingLine\"]," +
                    "\"additionalProperties\":false}}}";
            case LLMJsonSchema.ConversationScript:
                return "{\"type\":\"json_schema\",\"json_schema\":{" +
                    "\"name\":\"conversation_script\",\"strict\":true,\"schema\":{" +
                    "\"type\":\"object\",\"properties\":{" +
                    "\"reply1\":{\"type\":\"string\"}," +
                    "\"reply2\":{\"type\":\"string\"}," +
                    "\"reply3\":{\"type\":\"string\"}}," +
                    "\"required\":[\"reply1\",\"reply2\",\"reply3\"]," +
                    "\"additionalProperties\":false}}}";
            default:
                return "{\"type\":\"json_object\"}";
        }
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

    [Serializable]
    private class ChatCompletionResponse
    {
        public Choice[] choices;
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
}
