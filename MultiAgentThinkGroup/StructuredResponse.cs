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
    /// JSON Schema for StructuredResponse, suitable for xAI structured outputs.
    /// </summary>
    public static object GetSchema() => new
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
            additionalProperties = false
        },
        strict = true
    };
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