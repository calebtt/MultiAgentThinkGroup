using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Grok.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.SemanticKernel.Connectors.Grok.Core;

internal sealed class GrokChatCompletionClient : GrokClientBase
{
    private readonly string _modelId;
    private readonly Uri _chatGenerationEndpoint;
    private readonly ILogger _logger;

    internal GrokChatCompletionClient(
        HttpClient httpClient,
        string modelId,
        string apiKey,
        string apiVersion = "v1",
        ILogger? logger = null)
        : base(httpClient, logger, apiKey)
    {
        if (httpClient.BaseAddress is null)
        {
            throw new ArgumentException("HttpClient.BaseAddress must be set to the Grok API base URL (e.g. https://api.x.ai/).", nameof(httpClient));
        }

        this._modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        this._logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        this._chatGenerationEndpoint = new Uri(httpClient.BaseAddress, $"{apiVersion}/chat/completions");
    }

    public async Task<IReadOnlyList<ChatMessageContent>> CompleteChatAsync(
        ChatHistory chatHistory,
        GrokPromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        if (chatHistory is null) throw new ArgumentNullException(nameof(chatHistory));

        var grokRequest = new GrokRequest
        {
            Messages = new List<GrokMessage>()
        };

        // Populate messages from history
        foreach (var (role, content) in ExtractMessagesFromChatHistory(chatHistory))
        {
            grokRequest.Messages.Add(new GrokMessage
            {
                Role = role,
                Content = content ?? string.Empty
            });
        }

        if (grokRequest.Messages.Count == 0)
        {
            throw new InvalidOperationException("Chat history does not contain any messages");
        }

        // Add tool definitions if tool calling is enabled
        if (executionSettings?.ToolCallBehavior != null && kernel != null)
        {
            grokRequest.Tools = GetToolDefinitions(kernel);
        }

        // Map structured output mode enum to xAI string value, if any.
        string? structuredMode = executionSettings?.StructuredOutputMode switch
        {
            GrokStructuredOutputMode.JsonSchema => "json_schema",
            GrokStructuredOutputMode.ToolStrict => "tool_strict",
            _ => null
        };

        // Build response_format only when we have a schema AND we're in json_schema mode.
        object? responseFormat = null;
        if (executionSettings?.StructuredOutputSchema is not null &&
            string.Equals(structuredMode, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            responseFormat = new
            {
                type = "json_schema",
                json_schema = executionSettings.StructuredOutputSchema
            };
        }

        var requestData = new
        {
            model = this._modelId,
            messages = grokRequest.Messages,
            tools = grokRequest.Tools,
            stream = false,
            // xAI-specific bits:
            n = executionSettings?.CandidateCount,
            xai_structured_output_mode = structuredMode,
            response_format = responseFormat
        };


        var request = await CreateHttpRequestAsync(requestData, _chatGenerationEndpoint, cancellationToken)
            .ConfigureAwait(false);

        var responseString = await SendRequestAndGetStringBodyAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var response = DeserializeResponse<GrokResponse>(responseString);

        if (response.Choices is null || response.Choices.Count == 0)
        {
            throw new InvalidOperationException("No choices returned");
        }

        var choice = response.Choices[0];
        var grokMessage = choice.Message;

        var rawContent = grokMessage.Content ?? string.Empty;

        // Base SK message
        var message = new ChatMessageContent(AuthorRole.Assistant, rawContent);

        // NEW: surface Grok tool_calls as SK FunctionCallContent
        if (grokMessage.ToolCalls != null)
        {
            foreach (var toolCall in grokMessage.ToolCalls)
            {
                if (toolCall.Function?.Name is not { } functionName || string.IsNullOrWhiteSpace(functionName))
                {
                    continue;
                }

                var id = toolCall.Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = Guid.NewGuid().ToString("N");
                }

                var argsJson = toolCall.Function.Arguments ?? "{}";
                var arguments = BuildKernelArgumentsFromJsonString(argsJson);

                var functionCall = new FunctionCallContent(
                    functionName: functionName,
                    pluginName: null, // Grok tools aren't plugin-scoped
                    id: id,
                    arguments: arguments);

                message.Items.Add(functionCall);
            }
        }

        // OPTIONAL: keep your "content contains JSON" fallback, if you want:
        // if (!string.IsNullOrWhiteSpace(rawContent) && rawContent.TrimStart().StartsWith("{")) { ... }

        return new List<ChatMessageContent> { message };
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> StreamCompleteChatAsync(
        ChatHistory chatHistory,
        GrokPromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatHistory is null) throw new ArgumentNullException(nameof(chatHistory));

        var grokRequest = new GrokRequest
        {
            Messages = new List<GrokMessage>()
        };

        // Populate messages from history
        foreach (var (role, content) in ExtractMessagesFromChatHistory(chatHistory))
        {
            grokRequest.Messages.Add(new GrokMessage
            {
                Role = role,
                Content = content ?? string.Empty
            });
        }

        if (grokRequest.Messages.Count == 0)
        {
            throw new InvalidOperationException("Chat history does not contain any messages");
        }

        // Add tool definitions if tool calling is enabled
        if (executionSettings?.ToolCallBehavior != null && kernel != null)
        {
            grokRequest.Tools = GetToolDefinitions(kernel);
        }

        // Map structured output mode enum to xAI string value, if any.
        string? structuredMode = executionSettings?.StructuredOutputMode switch
        {
            GrokStructuredOutputMode.JsonSchema => "json_schema",
            GrokStructuredOutputMode.ToolStrict => "tool_strict",
            _ => null
        };

        // Build response_format only when we have a schema AND we're in json_schema mode.
        object? responseFormat = null;
        if (executionSettings?.StructuredOutputSchema is not null &&
            string.Equals(structuredMode, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            responseFormat = new
            {
                type = "json_schema",
                json_schema = executionSettings.StructuredOutputSchema
            };
        }

        var requestData = new
        {
            model = this._modelId,
            messages = grokRequest.Messages,
            tools = grokRequest.Tools,
            stream = true, // streaming mode
            n = executionSettings?.CandidateCount,
            xai_structured_output_mode = structuredMode,
            response_format = responseFormat
        };


        var request = await CreateHttpRequestAsync(requestData, _chatGenerationEndpoint, cancellationToken)
            .ConfigureAwait(false);

        HttpResponseMessage httpResponse;
        try
        {
            // Uses GrokClientBase helper that reads error body on non-success
            httpResponse = await SendRequestAndGetResponseImmediatelyAfterHeadersReadAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            this._logger.LogError(ex, "Grok streaming request failed.");
            throw;
        }

        using (httpResponse)
        using (var responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        using (var reader = new StreamReader(responseStream))
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    // End of stream
                    yield break;
                }

                // Skip comments / keep-alives
                if (string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith(":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Strip "data: " prefix if present
                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    line = line.Substring("data:".Length).Trim();
                }

                if (string.Equals(line, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    yield break;
                }

                // Try to interpret the payload as JSON with { choices[0].delta.content } etc.
                string? text = null;
                try
                {
                    text = TryExtractTextFromPossibleJson(line);
                }
                catch (JsonException)
                {
                    // malformed chunk; ignore and continue
                }

                if (!string.IsNullOrEmpty(text))
                {
                    yield return new StreamingChatMessageContent(AuthorRole.Assistant, text);
                }
            }
        }
    }


    // Helper: Convert kernel plugins/functions to Grok/OpenAI tool format
    private static object? GetToolDefinitions(Kernel kernel)
    {
        var functions = kernel.Plugins
            .SelectMany(p => p.GetFunctionsMetadata())
            .ToList();

        if (!functions.Any()) return null;

        return functions.Select(f => new
        {
            type = "function",
            function = new
            {
                name = f.Name,
                description = f.Description ?? "No description",
                parameters = new
                {
                    type = "object",
                    properties = f.Parameters.ToDictionary(
                        p => p.Name,
                        p => new
                        {
                            type = MapToJsonSchemaType(p.ParameterType),
                            description = p.Description ?? string.Empty
                        }),
                    required = f.Parameters
                        .Where(p => p.IsRequired)
                        .Select(p => p.Name)
                        .ToArray()
                }
            }
        }).ToArray();
    }

    // Map .NET types to valid JSON Schema types
    private static string MapToJsonSchemaType(Type? type)
    {
        if (type == null)
        {
            return "string";
        }

        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string))
        {
            return "string";
        }

        if (t == typeof(bool))
        {
            return "boolean";
        }

        if (t == typeof(int) || t == typeof(long) || t == typeof(short) ||
            t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) ||
            t == typeof(ushort) || t == typeof(sbyte))
        {
            return "integer";
        }

        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
        {
            return "number";
        }

        // Treat collections as arrays
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && t != typeof(string))
        {
            return "array";
        }

        // Fallback: stringify complex types
        return "string";
    }

    #region ChatHistory extraction helpers

    private static IEnumerable<(string Role, string? Content)> ExtractMessagesFromChatHistory(ChatHistory chatHistory)
    {
        foreach (var message in chatHistory)
        {
            if (message is not ChatMessageContent chatMessage)
            {
                continue;
            }

            var role = MapAuthorRoleToString(chatMessage.Role);
            string? content = chatMessage.Content;

            // If Content is empty but there are Items (TextContent, FunctionResultContent, etc),
            // flatten those into a single string so Grok sees the tool results.
            if (string.IsNullOrWhiteSpace(content) &&
                chatMessage.Items is not null &&
                chatMessage.Items.Count > 0)
            {
                var sb = new StringBuilder();

                foreach (var item in chatMessage.Items)
                {
                    switch (item)
                    {
                        case TextContent text:
                            // Normal conversational text
                            sb.Append(text.Text);
                            break;

                        case FunctionResultContent fnResult:
                            // Tool result from SK – serialize to something Grok can consume.
                            // Prefer raw string / JSON, fall back to JSON serialization.
                            if (fnResult.Result is string s)
                            {
                                sb.Append(s);
                            }
                            else if (fnResult.Result is JsonElement je)
                            {
                                sb.Append(je.GetRawText());
                            }
                            else if (fnResult.Result is not null)
                            {
                                sb.Append(JsonSerializer.Serialize(fnResult.Result));
                            }
                            break;

                        default:
                            // Fallback: don’t lose information, even if we don’t know the item type.
                            sb.Append(item.ToString());
                            break;
                    }
                }

                if (sb.Length > 0)
                {
                    content = sb.ToString();
                }
            }

            yield return (role, content);
        }
    }

    #endregion

    private static string? TryExtractTextFromPossibleJson(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // delta-style: root.choices[0].delta.content
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("content", out var contentEl))
                {
                    return contentEl.GetString();
                }

                if (first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content2))
                {
                    return content2.GetString();
                }
            }

            if (root.TryGetProperty("delta", out var deltaRoot) && deltaRoot.ValueKind == JsonValueKind.Object && deltaRoot.TryGetProperty("content", out var contentDelta))
            {
                return contentDelta.GetString();
            }

            if (root.TryGetProperty("text", out var textEl))
            {
                return textEl.GetString();
            }

            if (root.TryGetProperty("content", out var contentEl2))
            {
                return contentEl2.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string MapAuthorRoleToString(Microsoft.SemanticKernel.ChatCompletion.AuthorRole role)
    {
        if (role == Microsoft.SemanticKernel.ChatCompletion.AuthorRole.User) return "user";
        if (role == Microsoft.SemanticKernel.ChatCompletion.AuthorRole.Assistant) return "assistant";
        if (role == Microsoft.SemanticKernel.ChatCompletion.AuthorRole.System) return "system";
        if (role == Microsoft.SemanticKernel.ChatCompletion.AuthorRole.Tool) return "tool";
        return role.ToString().ToLowerInvariant();
    }

    private static KernelArguments BuildKernelArgumentsFromJsonString(string json)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                       ?? new Dictionary<string, JsonElement>();

            var args = new KernelArguments();
            foreach (var (key, value) in dict)
            {
                object? converted = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.TryGetInt64(out var i) ? i : value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                    JsonValueKind.Null => null,
                    _ => value.ToString()
                };

                args[key] = converted;
            }

            return args;
        }
        catch
        {
            // Fallback: keep the raw JSON as a single parameter
            return new KernelArguments
            {
                ["json"] = json
            };
        }
    }


}

