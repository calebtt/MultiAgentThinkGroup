using Microsoft.Extensions.AI; // For ChatResponseFormat
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAgentThinkGroup;

public record StructuredResponse(
    [property: JsonPropertyName("reasoning")] IReadOnlyList<string> Reasoning,
    [property: JsonPropertyName("final_answer")] string FinalAnswer,
    [property: JsonPropertyName("confidence")] double Confidence = 1.0,
    [property: JsonPropertyName("sources")] string[]? Sources = null
)
{
    /// <summary>
    /// JSON Schema wrapper for xAI / Grok structured outputs.
    /// </summary>
    public static object GetXaiSchema() => new
    {
        name = "structured_response",
        schema = new
        {
            type = "object",
            properties = new
            {
                reasoning = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Short, user-facing explanations or key considerations."
                },
                final_answer = new
                {
                    type = "string",
                    description = "Your final answer for the user."
                },
                confidence = new
                {
                    type = "number",
                    minimum = 0.0,
                    maximum = 1.0,
                    description = "Confidence score between 0.0 and 1.0."
                },
                sources = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Optional list of sources or URLs, if any."
                }
            },
            required = new[] { "final_answer", "confidence" },
            additionalProperties = false
        },
        strict = true
    };

    // Core JSON Schema – plain object, no xAI wrapper.
    private const string CoreSchemaJson = """
    {
      "type": "object",
      "properties": {
        "reasoning": {
          "type": "array",
          "items": { "type": "string" }
        },
        "final_answer": {
          "type": "string"
        },
        "confidence": {
          "type": "number",
          "minimum": 0.0,
          "maximum": 1.0
        },
        "sources": {
          "type": "array",
          "items": { "type": "string" }
        }
      },
      "required": ["final_answer", "confidence"]
    }
    """;

    /// <summary>
    /// Plain JSON schema as JsonElement, used *as-is* for Gemini (response_schema).
    /// </summary>
    public static JsonElement GetCoreJsonSchemaElement()
        => JsonDocument.Parse(CoreSchemaJson).RootElement.Clone();

    /// <summary>
    /// OpenAI / ChatGPT structured-output format wrapper around the core schema.
    /// This becomes the 'response_format' for OpenAI.
    /// </summary>
    public static ChatResponseFormat GetOpenAiResponseFormat()
        => ChatResponseFormat.ForJsonSchema(
            GetCoreJsonSchemaElement(),
            schemaName: "structured_response",
            schemaDescription: "Structured response with reasoning, final_answer, confidence, and optional sources.");

    public override string ToString()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Confidence: {Confidence:0.00}");

        if (Reasoning is { Count: > 0 })
        {
            sb.AppendLine("Reasoning:");
            for (int i = 0; i < Reasoning.Count; i++)
            {
                sb.Append("  ");
                sb.Append(i + 1);
                sb.Append(". ");
                sb.AppendLine(Reasoning[i]);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Final answer:");
        sb.AppendLine(FinalAnswer);

        if (Sources is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Sources:");
            for (int i = 0; i < Sources.Length; i++)
            {
                sb.Append("  - ");
                sb.AppendLine(Sources[i]);
            }
        }

        return sb.ToString();
    }
}

public class StructuredResponseEventArgs : EventArgs
{
    public string AgentName { get; }
    public StructuredResponse Response { get; }

    public StructuredResponseEventArgs(string agentName, StructuredResponse response)
    {
        AgentName = agentName;
        Response = response;
    }
}