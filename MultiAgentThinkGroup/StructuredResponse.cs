using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI; // For ChatResponseFormat

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