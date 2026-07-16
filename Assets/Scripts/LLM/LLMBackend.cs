using System;
using System.Collections.Generic;
using System.Text;
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
    public int timeoutSeconds = 30;
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

        RequestPayload payload = new()
        {
            model = model,
            messages = messages,
            temperature = options.temperature,
            max_tokens = options.maxTokens,
            stream = false
        };

        if (options.jsonMode)
            payload.response_format = new ResponseFormat { type = "json_object" };

        string json = JsonUtility.ToJson(payload);

        using (UnityWebRequest req = new(baseUrl + "/chat/completions", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(apiKey))
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);
            req.timeout = (options != null && options.timeoutSeconds > 0) ? options.timeoutSeconds : 30;

            UnityWebRequest.Result result = await WebRequestTask(req);

            if (result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning(nameof(OpenAICompatibleBackend) + " request failed: " + req.error);
                return null;
            }

            ChatCompletionResponse resp = JsonUtility.FromJson<ChatCompletionResponse>(req.downloadHandler.text);
            if (resp == null || resp.choices == null || resp.choices.Length == 0)
                return null;

            return resp.choices[0].message.content;
        }
    }

    private static Task<UnityWebRequest.Result> WebRequestTask(UnityWebRequest req)
    {
        return WaitForWebRequest(req);
    }

    private static async Task<UnityWebRequest.Result> WaitForWebRequest(UnityWebRequest req)
    {
        UnityWebRequestAsyncOperation op = req.SendWebRequest();

        // Do not subscribe a managed delegate to AsyncOperation.completed here.
        // Unity can keep that native operation alive across an Editor domain reload,
        // then try to release a GC handle owned by the previous scripting domain.
        while (!op.isDone)
            await Task.Yield();

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
        public ResponseFormat response_format;
    }

    [Serializable]
    private class ResponseFormat
    {
        public string type;
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
