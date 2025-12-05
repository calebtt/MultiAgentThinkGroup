using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

public class GrokCompletionService : IChatCompletionService
{
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly string _modelName;

    public GrokCompletionService(string grokApiKey, string modelName)
    {
        _apiKey = grokApiKey;
        _modelName = modelName;
        _client = new HttpClient { BaseAddress = new Uri("https://api.x.ai/v1/") };
    }

    public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?> { { "Model", _modelName } };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = chatHistory.Select(m => new
            {
                role = m.Role.Label,
                content = new[] { new { type = "text", text = m.Content ?? "" } }  // Wrap content in array; use empty string if null
            }).ToArray();

            var payload = new { model = _modelName, messages };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _client.PostAsync("chat/completions", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Error($"API Error: Status Code {response.StatusCode}");
                Log.Error($"Reason Phrase: {response.ReasonPhrase}");
                Log.Error($"Response Content: {errorContent}");
                Log.Error($"Request Payload: {JsonSerializer.Serialize(payload)}");
                throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Error Content: {errorContent}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var choice = result.GetProperty("choices")[0];
            var messageContent = choice.GetProperty("message").GetProperty("content").GetString();
            return [new ChatMessageContent(AuthorRole.Assistant, messageContent)];
        }
        catch (Exception ex)
        {
            Log.Error($"Exception in Grok API call: {ex.Message}");
            Log.Error($"Stack Trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Log.Error($"Inner Exception: {ex.InnerException.Message}");
            }
            throw;  // Rethrow to propagate the error
        }
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not supported in this example.");
    }
}