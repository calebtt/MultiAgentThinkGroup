using Microsoft.SemanticKernel;
using System.Text.Json.Serialization;

namespace Microsoft.SemanticKernel.Connectors.Grok;

public class GrokPromptExecutionSettings : PromptExecutionSettings
{
    public GrokToolCallBehavior? ToolCallBehavior { get; set; }

    /// <summary>
    /// Number of candidates (choices) to generate, if supported by the model.
    /// Maps to the 'n' field in the xAI chat/completions API.
    /// </summary>
    public int? CandidateCount { get; set; } = 1;

    /// <summary>
    /// Structured output mode for xAI / Grok.
    /// When set, maps to 'xai_structured_output_mode' in the request body.
    /// </summary>
    public GrokStructuredOutputMode StructuredOutputMode { get; set; } = GrokStructuredOutputMode.None;

    /// <summary>
    /// Optional JSON Schema (or schema-like object) to enforce when using
    /// StructuredOutputMode.JsonSchema. This should be a JSON-serializable object
    /// (e.g. an anonymous type, Dictionary&lt;string, object&gt;, or a POCO).
    ///
    /// It will be sent under:
    ///   response_format: { type: "json_schema", json_schema: &lt;this object&gt; }
    /// </summary>
    public object? StructuredOutputSchema { get; set; }
}

public static class GrokPromptExecutionSettingsExtensions
{
    /// <summary>
    /// Configure this settings instance to use xAI's JSON Schema based structured outputs.
    /// This sets StructuredOutputMode to JsonSchema and assigns the given schema object.
    ///
    /// The schema object should be a JSON-serializable structure (anonymous type,
    /// Dictionary&lt;string, object&gt;, POCO, etc.) matching the shape expected by xAI.
    /// </summary>
    /// <param name="settings">Settings instance to configure.</param>
    /// <param name="jsonSchema">JSON schema object to send in response_format.json_schema.</param>
    /// <returns>The same settings instance for chaining.</returns>
    public static GrokPromptExecutionSettings UseJsonSchema(
        this GrokPromptExecutionSettings settings,
        object jsonSchema)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (jsonSchema is null) throw new ArgumentNullException(nameof(jsonSchema));

        settings.StructuredOutputMode = GrokStructuredOutputMode.JsonSchema;
        settings.StructuredOutputSchema = jsonSchema;
        return settings;
    }

    /// <summary>
    /// Configure this settings instance to use xAI's tool_strict structured output mode
    /// (no JSON schema payload is sent).
    /// </summary>
    public static GrokPromptExecutionSettings UseToolStrict(
        this GrokPromptExecutionSettings settings)
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


public class GrokToolCallBehavior
{
    public static GrokToolCallBehavior AutoInvokeKernelFunctions => new GrokToolCallBehavior();
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

    // NEW: for responses from Grok
    [JsonPropertyName("tool_calls")]
    public List<GrokToolCall>? ToolCalls { get; set; }

    // Optional, Grok includes it in some responses
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