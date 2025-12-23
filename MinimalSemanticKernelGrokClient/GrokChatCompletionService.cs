using Matg.SemanticKernel.Connectors.Grok.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Matg.SemanticKernel.Connectors.Grok;

public sealed class GrokChatCompletionService : IChatCompletionService
{
    private const int MaxAutoInvokeSteps = 8;

    private readonly Dictionary<string, object?> _attributesInternal = new();
    private readonly GrokChatCompletionClient _chatCompletionClient;

    public IReadOnlyDictionary<string, object?> Attributes => this._attributesInternal;

    public GrokChatCompletionService(
        string modelId,
        string apiKey,
        string apiVersion = "v1",
        HttpClient? httpClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentNullException(nameof(modelId));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentNullException(nameof(apiKey));

        var client = httpClient ?? new HttpClient();
        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri("https://api.x.ai/");
        }

        this._chatCompletionClient = new GrokChatCompletionClient(
            httpClient: client,
            modelId: modelId,
            apiKey: apiKey,
            apiVersion: apiVersion,
            logger: loggerFactory?.CreateLogger(typeof(GrokChatCompletionService)));

        this._attributesInternal.Add(AIServiceExtensions.ModelIdKey, modelId);
    }

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var grokSettings = NormalizeSettings(executionSettings);

        // If tool calling is not enabled (or no kernel), behave exactly like before.
        if (grokSettings.ToolCallBehavior is null || kernel is null)
        {
            return await this._chatCompletionClient
                .CompleteChatAsync(chatHistory, grokSettings, kernel, cancellationToken)
                .ConfigureAwait(false);
        }

        // Minimal "auto tool calling" loop (non-streaming)
        var working = Clone(chatHistory);

        for (var step = 0; step < MaxAutoInvokeSteps; step++)
        {
            var assistantMessages = await this._chatCompletionClient
                .CompleteChatAsync(working, grokSettings, kernel, cancellationToken)
                .ConfigureAwait(false);

            var assistant = assistantMessages[0];
            working.Add(assistant);

            var calls = FunctionCallContent.GetFunctionCalls(assistant).ToArray();
            if (calls.Length == 0)
            {
                return assistantMessages;
            }

            foreach (var call in calls)
            {
                // Invoke the function and append a role=tool message to history.
                var result = await call.InvokeAsync(kernel, cancellationToken).ConfigureAwait(false);
                working.Add(result.ToChatMessage());
            }
        }

        throw new InvalidOperationException(
            $"Tool calling exceeded max steps ({MaxAutoInvokeSteps}). " +
            "Likely the model is stuck repeatedly requesting tools.");
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var grokSettings = NormalizeSettings(executionSettings);

        // Streaming implementation intentionally does not auto-invoke tools.
        return this._chatCompletionClient.StreamCompleteChatAsync(chatHistory, grokSettings, kernel, cancellationToken);
    }

    private static ChatHistory Clone(ChatHistory original)
    {
        var copy = new ChatHistory();
        foreach (var m in original)
        {
            copy.Add(m);
        }
        return copy;
    }

    private static GrokPromptExecutionSettings NormalizeSettings(PromptExecutionSettings? executionSettings)
    {
        if (executionSettings is GrokPromptExecutionSettings grok)
        {
            return grok;
        }

        var grokSettings = new GrokPromptExecutionSettings();

        // Bridge OpenAI-style settings where it makes sense.
        if (executionSettings is OpenAIPromptExecutionSettings openAi)
        {
            if (openAi.ToolCallBehavior != null)
            {
                grokSettings.ToolCallBehavior = GrokToolCallBehavior.AutoInvokeKernelFunctions;
            }

            grokSettings.Temperature = openAi.Temperature;
            grokSettings.TopP = openAi.TopP;
            grokSettings.MaxTokens = openAi.MaxTokens;
            grokSettings.StopSequences = openAi.StopSequences is null ? null : new List<string>(openAi.StopSequences);
            grokSettings.PresencePenalty = openAi.PresencePenalty;
            grokSettings.FrequencyPenalty = openAi.FrequencyPenalty;
        }

        return grokSettings;
    }
}

public static class KernelBuilderExtensions
{
    public static IKernelBuilder AddGrokChatCompletion(
        this IKernelBuilder builder,
        string modelId,
        string apiKey,
        string apiVersion = "v1",
        HttpClient? httpClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        builder.Services.AddSingleton<IChatCompletionService>(sp =>
            new GrokChatCompletionService(
                modelId,
                apiKey,
                apiVersion,
                httpClient,
                loggerFactory ?? sp.GetService<ILoggerFactory>()));

        return builder;
    }
}