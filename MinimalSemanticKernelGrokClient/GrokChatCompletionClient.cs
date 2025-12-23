using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Matg.SemanticKernel.Connectors.Grok.Core;

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
        this._logger = logger ?? NullLogger.Instance;

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
            Messages = ConvertChatHistoryToGrokMessages(chatHistory).ToList()
        };

        if (grokRequest.Messages.Count == 0)
        {
            throw new InvalidOperationException("Chat history does not contain any messages");
        }

        // Add tool definitions if tool calling is enabled
        // FIX: Only set tools if there are actual functions defined
        object? tools = null;
        if (executionSettings?.ToolCallBehavior != null && kernel != null)
        {
            tools = GetToolDefinitions(kernel);
        }

        // Map structured output mode enum to xAI string value, if any.
        string? structuredMode = executionSettings?.StructuredOutputMode switch
        {
            GrokStructuredOutputMode.JsonSchema => "json_schema",
            GrokStructuredOutputMode.ToolStrict => "tool_strict",
            _ => null
        };

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
            tools = tools,  // Will be null if no tools, which gets omitted from JSON
            stream = false,

            n = executionSettings?.CandidateCount,
            temperature = executionSettings?.Temperature,
            top_p = executionSettings?.TopP,
            max_tokens = executionSettings?.MaxTokens,
            stop = executionSettings?.StopSequences,
            presence_penalty = executionSettings?.PresencePenalty,
            frequency_penalty = executionSettings?.FrequencyPenalty,

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

        var grokMessage = response.Choices[0].Message;
        var rawContent = grokMessage.Content ?? string.Empty;

        var message = new ChatMessageContent(AuthorRole.Assistant, rawContent);

        // Surface Grok tool_calls as SK FunctionCallContent
        if (grokMessage.ToolCalls != null)
        {
            foreach (var toolCall in grokMessage.ToolCalls)
            {
                var rawName = toolCall.Function?.Name;
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                var id = toolCall.Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = Guid.NewGuid().ToString("N");
                }

                // We encode tool names as "Plugin-Function" when possible.
                SplitToolName(rawName!, out var pluginName, out var functionName);

                var argsJson = toolCall.Function?.Arguments ?? "{}";
                var arguments = BuildKernelArgumentsFromJsonString(argsJson);

                var functionCall = new FunctionCallContent(
                    functionName: functionName,
                    pluginName: pluginName,
                    id: id!,
                    arguments: arguments);

                message.Items.Add(functionCall);
            }
        }

        return new List<ChatMessageContent> { message };
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> StreamCompleteChatAsync(
        ChatHistory chatHistory,
        GrokPromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatHistory is null) throw new ArgumentNullException(nameof(chatHistory));

        // NOTE: This streaming path intentionally only streams text deltas.
        // Tool calling in streaming would require parsing tool_call deltas and then a follow-up non-streaming loop.

        var grokRequest = new GrokRequest
        {
            Messages = ConvertChatHistoryToGrokMessages(chatHistory).ToList()
        };

        if (grokRequest.Messages.Count == 0)
        {
            throw new InvalidOperationException("Chat history does not contain any messages");
        }

        object? tools = null;
        if (executionSettings?.ToolCallBehavior != null && kernel != null)
        {
            tools = GetToolDefinitions(kernel);
        }

        string? structuredMode = executionSettings?.StructuredOutputMode switch
        {
            GrokStructuredOutputMode.JsonSchema => "json_schema",
            GrokStructuredOutputMode.ToolStrict => "tool_strict",
            _ => null
        };

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
            tools = tools,
            stream = true,

            n = executionSettings?.CandidateCount,
            temperature = executionSettings?.Temperature,
            top_p = executionSettings?.TopP,
            max_tokens = executionSettings?.MaxTokens,
            stop = executionSettings?.StopSequences,
            presence_penalty = executionSettings?.PresencePenalty,
            frequency_penalty = executionSettings?.FrequencyPenalty,

            xai_structured_output_mode = structuredMode,
            response_format = responseFormat
        };

        var request = await CreateHttpRequestAsync(requestData, _chatGenerationEndpoint, cancellationToken)
            .ConfigureAwait(false);

        HttpResponseMessage httpResponse;
        try
        {
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
                // FIX: Use cancellation-aware read
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (line is null)
                {
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    line = line.Substring("data:".Length).Trim();
                }

                if (string.Equals(line, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    yield break;
                }

                string? text = null;
                try
                {
                    text = TryExtractTextFromPossibleJson(line);
                }
                catch (JsonException)
                {
                    // ignore malformed chunk
                }

                if (!string.IsNullOrEmpty(text))
                {
                    yield return new StreamingChatMessageContent(AuthorRole.Assistant, text);
                }
            }
        }
    }

    private static object? GetToolDefinitions(Kernel kernel)
    {
        var functions = kernel.Plugins
            .SelectMany(p => p.GetFunctionsMetadata())
            .ToList();

        // FIX: Return null instead of empty array when no tools
        if (functions.Count == 0) return null;

        return functions.Select(f =>
        {
            // Use hyphen separator to avoid conflicts with function names containing dots
            var fullName = string.IsNullOrWhiteSpace(f.PluginName) ? f.Name : $"{f.PluginName}-{f.Name}";

            return new
            {
                type = "function",
                function = new
                {
                    name = fullName,
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
            };
        }).ToArray();
    }

    private static string MapToJsonSchemaType(Type? type)
    {
        if (type == null) return "string";

        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string)) return "string";
        if (t == typeof(bool)) return "boolean";

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

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && t != typeof(string))
        {
            return "array";
        }

        return "string";
    }

    private static IEnumerable<GrokMessage> ConvertChatHistoryToGrokMessages(ChatHistory chatHistory)
    {
        foreach (var message in chatHistory)
        {
            if (message is not ChatMessageContent chatMessage)
            {
                continue;
            }

            var grok = new GrokMessage
            {
                Role = MapAuthorRoleToString(chatMessage.Role)
            };

            // 1) Tool result messages must be role=tool and include tool_call_id
            if (chatMessage.Role == AuthorRole.Tool)
            {
                // FIX: Try multiple sources for tool_call_id
                grok.ToolCallId = ExtractToolCallId(chatMessage);
                grok.Content = ExtractToolResultContent(chatMessage) ?? string.Empty;
                yield return grok;
                continue;
            }

            // 2) Assistant messages that contain FunctionCallContent must serialize tool_calls
            if (chatMessage.Role == AuthorRole.Assistant)
            {
                var calls = chatMessage.Items?.OfType<FunctionCallContent>().ToList();
                if (calls is { Count: > 0 })
                {
                    grok.ToolCalls = calls.Select(ToGrokToolCall).ToList();
                }
            }

            // Normal text content (or flattened TextContent items)
            grok.Content = ExtractTextContent(chatMessage);

            yield return grok;
        }
    }

    /// <summary>
    /// Extracts tool_call_id from a tool result message.
    /// Checks multiple possible locations since SK stores this differently depending on how the message was created.
    /// </summary>
    private static string? ExtractToolCallId(ChatMessageContent msg)
    {
        // First, check FunctionResultContent items - this is where SK usually stores the CallId
        if (msg.Items is { Count: > 0 })
        {
            var fnResult = msg.Items.OfType<FunctionResultContent>().FirstOrDefault();
            if (fnResult != null && !string.IsNullOrWhiteSpace(fnResult.CallId))
            {
                return fnResult.CallId;
            }
        }

        // Fallback: check metadata with various key names
        return TryGetToolCallIdFromMetadata(msg);
    }

    private static GrokToolCall ToGrokToolCall(FunctionCallContent call)
    {
        // Use hyphen separator to match GetToolDefinitions
        var fullName = string.IsNullOrWhiteSpace(call.PluginName)
            ? call.FunctionName
            : $"{call.PluginName}-{call.FunctionName}";

        var argsJson = SerializeKernelArguments(call.Arguments);

        return new GrokToolCall
        {
            Id = string.IsNullOrWhiteSpace(call.Id) ? Guid.NewGuid().ToString("N") : call.Id,
            Type = "function",
            Function = new GrokToolFunction
            {
                Name = fullName,
                Arguments = argsJson
            }
        };
    }

    private static string SerializeKernelArguments(KernelArguments? args)
    {
        if (args == null || args.Count == 0) return "{}";

        // KernelArguments is IEnumerable<KeyValuePair<string, object?>>
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in args)
        {
            dict[kvp.Key] = kvp.Value;
        }

        return JsonSerializer.Serialize(dict, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static string? ExtractToolResultContent(ChatMessageContent msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.Content))
        {
            return msg.Content;
        }

        if (msg.Items is null || msg.Items.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var item in msg.Items)
        {
            if (item is FunctionResultContent fnResult)
            {
                if (fnResult.Result is string s) sb.Append(s);
                else if (fnResult.Result is JsonElement je) sb.Append(je.GetRawText());
                else if (fnResult.Result is not null) sb.Append(JsonSerializer.Serialize(fnResult.Result));
            }
            else if (item is TextContent text)
            {
                sb.Append(text.Text);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? ExtractTextContent(ChatMessageContent msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.Content))
        {
            return msg.Content;
        }

        if (msg.Items is null || msg.Items.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var item in msg.Items)
        {
            if (item is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
            {
                sb.Append(text.Text);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? TryGetToolCallIdFromMetadata(ChatMessageContent msg)
    {
        if (msg.Metadata is null || msg.Metadata.Count == 0)
        {
            return null;
        }

        // Common keys used by SK/connectors
        string[] keys =
        [
            "tool_call_id",
            "tool_id",
            "toolId",
            "toolid",
            "call_id",
            "callId"
        ];

        foreach (var k in keys)
        {
            if (!msg.Metadata.TryGetValue(k, out var val) || val is null) continue;

            if (val is string s && !string.IsNullOrWhiteSpace(s)) return s;

            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String)
                {
                    var str = je.GetString();
                    if (!string.IsNullOrWhiteSpace(str)) return str;
                }
            }
        }

        return null;
    }

    private static void SplitToolName(string raw, out string? pluginName, out string functionName)
    {
        // We encode as "Plugin-Function". If not present, pluginName=null.
        var idx = raw.IndexOf('-');
        if (idx > 0 && idx < raw.Length - 1)
        {
            pluginName = raw.Substring(0, idx);
            functionName = raw.Substring(idx + 1);
            return;
        }

        pluginName = null;
        functionName = raw;
    }

    private static string? TryExtractTextFromPossibleJson(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var contentEl))
                {
                    return contentEl.GetString();
                }

                if (first.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.Object &&
                    msg.TryGetProperty("content", out var content2))
                {
                    return content2.GetString();
                }
            }

            if (root.TryGetProperty("delta", out var deltaRoot) &&
                deltaRoot.ValueKind == JsonValueKind.Object &&
                deltaRoot.TryGetProperty("content", out var contentDelta))
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

    private static string MapAuthorRoleToString(AuthorRole role)
    {
        if (role == AuthorRole.User) return "user";
        if (role == AuthorRole.Assistant) return "assistant";
        if (role == AuthorRole.System) return "system";
        if (role == AuthorRole.Tool) return "tool";
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
            return new KernelArguments
            {
                ["json"] = json
            };
        }
    }
}