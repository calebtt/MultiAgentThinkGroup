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

public class GrokCompletionService : IChatCompletionService
{
    private readonly HttpClient _client;
    private readonly string _apiKey;

    public GrokCompletionService()
    {
        _apiKey = Environment.GetEnvironmentVariable("GROK_API_KEY") ?? throw new InvalidOperationException("GROK_API_KEY not set.");
        _client = new HttpClient { BaseAddress = new Uri("https://api.x.ai/v1/") };
    }

    public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?> { { "Model", "grok-4-1-fast-reasoning" } };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = chatHistory.Select(m => new
            {
                role = m.Role.Label,
                content = new[] { new { type = "text", text = m.Content ?? "" } }  // Wrap content in array; use empty string if null
            }).ToArray();

            var payload = new { model = "grok-4-1-fast-reasoning", messages };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _client.PostAsync("chat/completions", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"API Error: Status Code {response.StatusCode}");
                Console.WriteLine($"Reason Phrase: {response.ReasonPhrase}");
                Console.WriteLine($"Response Content: {errorContent}");
                Console.WriteLine($"Request Payload: {JsonSerializer.Serialize(payload)}");
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
            Console.WriteLine($"Exception in Grok API call: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
            }
            throw;  // Rethrow to propagate the error
        }
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not supported in this example.");
    }
}