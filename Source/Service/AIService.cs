using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimTalk.Client;
using RimTalk.Data;
using RimTalk.Error;
using RimTalk.Util;

namespace RimTalk.Service;

public static class AIService
{
    private static string _instruction = "";
    private static bool _busy;
    private static bool _contextUpdating;
    private static bool _firstInstruction = true;

    /// <summary>
    /// Streaming chat that invokes callback as each player's dialogue is parsed
    /// </summary>
    public static async Task ChatStreaming<T>(TalkRequest request,
        List<(Role role, string message)> messages,
        Dictionary<string, T> players,
        Action<T, TalkResponse> onPlayerResponseReceived)
    {
        var currentMessages = new List<(Role role, string message)>(messages) { (Role.User, request.Prompt) };
        var initApiLog = ApiHistory.AddRequest(request, _instruction);
        var lastApiLog = initApiLog;

        _busy = true;
        try
        {
            var payload = await AIErrorHandler.HandleWithRetry(() =>
            {
                var client = AIClientFactory.GetAIClient();
                return client.GetStreamingChatCompletionAsync<TalkResponse>(_instruction, currentMessages,
                    talkResponse =>
                    {
                        if (!players.TryGetValue(talkResponse.Name, out var player))
                        {
                            return;
                        }

                        talkResponse.TalkType = request.TalkType;

                        // Add logs
                        int elapsedMs = (int)(DateTime.Now - lastApiLog.Timestamp).TotalMilliseconds;
                        if (lastApiLog == initApiLog)
                            elapsedMs -= lastApiLog.ElapsedMs;
                        
                        var newApiLog = ApiHistory.AddResponse(initApiLog.Id, talkResponse.Text, talkResponse.Name, talkResponse.InteractionRaw, elapsedMs:elapsedMs);
                        talkResponse.Id = newApiLog.Id;
                        
                        lastApiLog = newApiLog;

                        onPlayerResponseReceived?.Invoke(player, talkResponse);
                    });
            });

            // Try deserializing once again with all streaming chunks to make sure a correct format was returned
            try
            {
                if (payload == null) throw new Exception();
                JsonUtil.DeserializeFromJson<List<TalkResponse>>(payload.Response);
            }
            catch
            {
                initApiLog.Response = "Failed";
                return;
            }
            
            ApiHistory.UpdatePayload(initApiLog.Id, payload);

            Stats.IncrementCalls();
            Stats.IncrementTokens(payload.TokenCount);

            _firstInstruction = false;
        }
        finally
        {
            _busy = false;
        }
    }

    // Original non-streaming method
    public static async Task<List<TalkResponse>> Chat(TalkRequest request,
        List<(Role role, string message)> messages)
    {
        var currentMessages = new List<(Role role, string message)>(messages) { (Role.User, request.Prompt) };

        var apiLog = ApiHistory.AddRequest(request, _instruction);

        var payload = await ExecuteAIRequest(_instruction, currentMessages);

        if (payload == null)
        {
            apiLog.Response = "Failed";
            return null;
        }

        var talkResponses = JsonUtil.DeserializeFromJson<List<TalkResponse>>(payload.Response);

        if (talkResponses != null)
        {
            foreach (var talkResponse in talkResponses)
            {
                apiLog = ApiHistory.AddResponse(apiLog.Id, talkResponse.Text, talkResponse.Name, talkResponse.InteractionRaw, payload);
                talkResponse.Id = apiLog.Id;
            }
        }

        _firstInstruction = false;

        return talkResponses;
    }

    // One time query - used for generating persona, etc
    public static async Task<T> Query<T>(TalkRequest request) where T : class, IJsonData
    {
        List<(Role role, string message)> message = [(Role.User, request.Prompt)];

        var apiLog = ApiHistory.AddRequest(request, _instruction);

        var payload = await ExecuteAIRequest(_instruction, message);

        if (payload == null)
        {
            apiLog.Response = "Failed";
            return null;
        }

        var jsonData = JsonUtil.DeserializeFromJson<T>(payload.Response);

        ApiHistory.AddResponse(apiLog.Id, jsonData.GetText(), null, null, payload: payload);

        return jsonData;
    }

    /// <summary>
    /// Data class for ChromaDB search parameters returned by AI.
    /// </summary>
    public class ChromaSearchParams
    {
        public List<string> content = [];  // Search query for ChromaDB
        public int num { get; set; } = 5;    // Number of results to fetch (default 5)
    }

    /// <summary>
    /// Generates an optimized search query and desired result count for ChromaDB by querying AI.
    /// Converts the dialogue prompt into a semantic search query and determines how many historical results to retrieve.
    /// Returns both the search query and the number of results in a structured format.
    /// </summary>
    public static async Task<ChromaSearchParams> GenerateChromaSearchQueryAsync(string prompt)
    {
        try
        {
            Logger.Debug($"[AIService] Starting ChromaDB search query and result count generation");
            Logger.Debug($"[AIService] Original prompt length: {prompt.Length} chars");

            var searchQueryInstruction = Constant.AIPromptOfChromaSearchQueryGeneration;

            List<(Role role, string message)> messages = new()
            {
                (Role.User, searchQueryInstruction + "\n" + prompt)
            };

            Logger.Debug($"[AIService] Sending request to AI client for search query and result count");
            var payload = await ExecuteAIRequest("", messages);

            if (payload == null)
            {
                Logger.Warning($"[AIService] AI client returned null payload for search query generation");
                Logger.Warning($"[AIService] Falling back to original prompt with default count");
                return new ChromaSearchParams { content = [prompt], num = 5 };
            }

            try
            {
                string responseContent = payload.Response?.Trim() ?? "{}";
                Logger.Debug($"[AIService] Raw API response: {responseContent}");
                if (responseContent.StartsWith("```"))
                {
                    int start = responseContent.IndexOf('{');
                    int end = responseContent.LastIndexOf('}');
                    if (start != -1 && end != -1 && end > start)
                    {
                        responseContent = responseContent.Substring(start, end - start + 1);
                    }
                }
                responseContent = responseContent.Trim().TrimEnd(';').Trim();
                
                // Try to parse the response as ChromaSearchParams directly first
                try
                {
                    var result = JsonUtil.DeserializeFromJson<ChromaSearchParams>(responseContent);
                    if (result != null && result.content != null && result.content.Count > 0)
                    {
                        // Validate num is in reasonable range
                        if (result.num < 1) result.num = 1;
                        if (result.num > 10) result.num = 10;
                        
                        Logger.Debug($"[AIService] Generated search query and result count successfully");
                        Logger.Debug($"[AIService] Search query: {result.content}");
                        Logger.Debug($"[AIService] Result count: {result.num}");
                        return result;
                    }
                }
                catch (Exception directEx)
                {
                    Logger.Debug($"[AIService] Direct deserialization failed: {directEx.Message}");
                }
            }
            catch (Exception parseEx)
            {
                Logger.Warning($"[AIService] Failed to parse AI response: {parseEx.Message}");
                Logger.Debug($"[AIService] Raw response: {payload.Response}");
            }

            Logger.Warning($"[AIService] Falling back to original prompt with default count");
            return new ChromaSearchParams { content = [prompt], num = 5 };
        }
        catch (Exception ex)
        {
            Logger.Warning($"[AIService] Exception during search query generation: {ex.Message}");
            Logger.Warning($"[AIService] Stack trace: {ex.StackTrace}");
            Logger.Warning($"[AIService] Falling back to original prompt with default count");
            return new ChromaSearchParams { content = [prompt], num = 5 };
        }
    }

    private static async Task<Payload> ExecuteAIRequest(string instruction,
        List<(Role role, string message)> messages)
    {
        _busy = true;
        try
        {
            var payload = await AIErrorHandler.HandleWithRetry(() =>
                AIClientFactory.GetAIClient().GetChatCompletionAsync(instruction, messages)
            );

            if (payload == null)
                return null;

            Stats.IncrementCalls();
            Stats.IncrementTokens(payload.TokenCount);

            return payload;
        }
        finally
        {
            _busy = false;
        }
    }

    public static void UpdateContext(string context)
    {
        Logger.Debug($"UpdateContext:\n{context}");
        _instruction = context;
    }

    public static string GetContext()
    {
        return _instruction;
    }

    public static bool IsFirstInstruction()
    {
        return _firstInstruction;
    }

    public static bool IsBusy()
    {
        return _busy || _contextUpdating;
    }

    public static bool IsContextUpdating()
    {
        return _contextUpdating;
    }

    public static void Clear()
    {
        _busy = false;
        _contextUpdating = false;
        _firstInstruction = true;
        _instruction = "";
    }
}