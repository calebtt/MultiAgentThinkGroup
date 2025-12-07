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
            // reasoning and sources are optional; final_answer + confidence are required
            required = new[] { "final_answer", "confidence" },
            // you *can* keep this for xAI; Gemini doesn't need to see it
            additionalProperties = false
        },
        strict = true
    };

    // Core JSON Schema – plain object, no xAI wrapper, no $ref, only basic fields that Gemini supports.
    // This is what we’ll give to Gemini *and* use for OpenAI’s structured output.
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
    /// Plain JSON schema as JsonElement, used *as-is* for Gemini and OpenAI.
    /// </summary>
    public static JsonElement GetCoreJsonSchemaElement()
        => JsonDocument.Parse(CoreSchemaJson).RootElement.Clone();
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