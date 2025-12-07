using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Grok.Core;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.SemanticKernel.Connectors.Grok;

public sealed class GrokChatCompletionService : IChatCompletionService
{
    private readonly Dictionary<string, object?> _attributesInternal = new();
    private readonly GrokChatCompletionClient _chatCompletionClient;
    public IReadOnlyDictionary<string, object?> Attributes => this._attributesInternal;


    /// <summary>
    /// Initializes a new instance of the <see cref="GrokChatCompletionService"/> class.
    /// </summary>
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
        // Ensure BaseAddress is set by caller if relative endpoints are expected. Alternatively the client can contain full URIs.
        if (client.BaseAddress is null)
        {
            // Default to x.ai endpoint if none provided — adjust to real xAI endpoint as needed.
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

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var grokSettings = NormalizeSettings(executionSettings);
        return this._chatCompletionClient.CompleteChatAsync(chatHistory, grokSettings, kernel, cancellationToken);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var grokSettings = NormalizeSettings(executionSettings);
        return this._chatCompletionClient.StreamCompleteChatAsync(chatHistory, grokSettings, kernel, cancellationToken);
    }

    private static GrokPromptExecutionSettings NormalizeSettings(PromptExecutionSettings? executionSettings)
    {
        // If caller already gave us Grok settings, just use them.
        if (executionSettings is GrokPromptExecutionSettings grok)
        {
            return grok;
        }

        // Start with minimal Grok settings
        var grokSettings = new GrokPromptExecutionSettings();

        // Bridge OpenAI-style ToolCallBehavior to GrokToolCallBehavior
        if (executionSettings is OpenAIPromptExecutionSettings openAi &&
            openAi.ToolCallBehavior != null)
        {
            grokSettings.ToolCallBehavior = GrokToolCallBehavior.AutoInvokeKernelFunctions;
        }

        return grokSettings;
    }

}

public static class KernelBuilderExtensions
{
    public static IKernelBuilder AddGrokChatCompletion(this IKernelBuilder builder, string modelId, string apiKey, string apiVersion = "v1", HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null)
    {
        builder.Services.AddSingleton<IChatCompletionService>(sp =>
            new GrokChatCompletionService(modelId, apiKey, apiVersion, httpClient, loggerFactory ?? sp.GetService<ILoggerFactory>()));
        return builder;
    }
}


