using Microsoft.SemanticKernel;
using System.Text.Json.Serialization;

namespace Matg.SemanticKernel.Connectors.Grok;

public class GrokPromptExecutionSettings : PromptExecutionSettings
{
    /// <summary>
    /// Controls if / how tools are used.
    /// </summary>
    public GrokToolCallBehavior? ToolCallBehavior { get; set; }

    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    public IList<string>? StopSequences { get; set; }
    public double? PresencePenalty { get; set; }
    public double? FrequencyPenalty { get; set; }

    /// <summary>
    /// Number of candidates (choices) to generate, if supported by the model.
    /// Maps to 'n'.
    /// </summary>
    public int? CandidateCount { get; set; } = 1;

    /// <summary>
    /// Structured output mode for xAI / Grok.
    /// When set, maps to 'xai_structured_output_mode' in the request body.
    /// </summary>
    public GrokStructuredOutputMode StructuredOutputMode { get; set; } = GrokStructuredOutputMode.None;

    /// <summary>
    /// Optional JSON Schema object to enforce when using StructuredOutputMode.JsonSchema.
    /// Sent as: response_format: { type: "json_schema", json_schema: <this> }
    /// </summary>
    public object? StructuredOutputSchema { get; set; }
}

public static class GrokPromptExecutionSettingsExtensions
{
    public static GrokPromptExecutionSettings UseJsonSchema(this GrokPromptExecutionSettings settings, object jsonSchema)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (jsonSchema is null) throw new ArgumentNullException(nameof(jsonSchema));

        settings.StructuredOutputMode = GrokStructuredOutputMode.JsonSchema;
        settings.StructuredOutputSchema = jsonSchema;
        return settings;
    }

    public static GrokPromptExecutionSettings UseToolStrict(this GrokPromptExecutionSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        settings.StructuredOutputMode = GrokStructuredOutputMode.ToolStrict;
        settings.StructuredOutputSchema = null;
        return settings;
    }
}

/// <summary>
/// xAI structured output modes for Grok.
/// These map directly to values for 'xai_structured_output_mode'.
/// </summary>
public enum GrokStructuredOutputMode
{
    None = 0,
    JsonSchema,
    ToolStrict
}

public sealed class GrokToolCallBehavior
{
    // Singleton is fine; you only use “!= null” checks today.
    public static readonly GrokToolCallBehavior AutoInvokeKernelFunctions = new();
}

public static class AIServiceExtensions
{
    public const string ModelIdKey = "model.id";
}

public sealed class GrokRequest
{
    [JsonPropertyName("messages")]
    public List<GrokMessage> Messages { get; set; } = new();

    [JsonPropertyName("tools")]
    public object? Tools { get; set; }
}

public sealed class GrokMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    // Responses from Grok (assistant tool calls)
    [JsonPropertyName("tool_calls")]
    public List<GrokToolCall>? ToolCalls { get; set; }

    // Requests to Grok (tool result messages need this)
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("refusal")]
    public object? Refusal { get; set; }
}

public sealed class GrokToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public GrokToolFunction? Function { get; set; }
}

public sealed class GrokToolFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // Grok returns this as a JSON string: "{\"query\":\"...\",\"count\":10}"
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

public sealed class GrokResponse
{
    [JsonPropertyName("choices")]
    public List<GrokResponseChoice> Choices { get; set; } = new();
}

public sealed class GrokResponseChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public GrokMessage Message { get; set; } = new GrokMessage();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}
