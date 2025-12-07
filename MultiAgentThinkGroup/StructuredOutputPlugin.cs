using Microsoft.SemanticKernel;
using Serilog;
using System.ComponentModel;  // For [Description]
using System.Text.Json;
using System.Text.RegularExpressions;  // For Regex

namespace MultiAgentThinkGroup;

/// <summary>
/// Plugin for submitting structured responses from agents.
/// Raises an event on successful submission for orchestrator to capture.
/// </summary>
public class StructuredOutputPlugin
{
    /// <summary>
    /// Event raised when a structured response is successfully submitted.
    /// </summary>
    public event EventHandler<StructuredResponseEventArgs>? ResponseSubmitted;

    /// <summary>
    /// Submits the final structured reasoning and answer.
    /// </summary>
    [KernelFunction("submit_structured_response")]
    [Description("""
Submit the final structured response.

CRITICAL OUTPUT RULES:
- Your ENTIRE reply MUST be a single function call to submit_structured_response.
- Do NOT output ANY text before or after the function call.
- Do NOT output markdown, headings, explanations, tags, code blocks, or commentary.
- Do NOT output multiple JSON objects or partial attempts.
- The argument MUST be valid JSON. No trailing characters.
- NOTHING except the function call is allowed in your response.

Example of the ONLY valid response form:

{
  "reasoning": ["..."],
  "final_answer": "...",
  "confidence": 0.95,
  "sources": ["..."]
}

Then wrap it in a function call. Nothing else.
""")]
    public StructuredResponse? SubmitStructuredResponse(
        [Description("JSON string representing the structured response")] string jsonInput)

    {
        try
        {
            // Escape common controls
            jsonInput = jsonInput.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

            // Fix invalid escapes added by the model (e.g., turn "don\'t" into "don't")
            jsonInput = jsonInput.Replace("\\'", "'");

            // Optional: Use regex to remove any other invalid backslashes (e.g., before non-escapable chars)
            jsonInput = Regex.Replace(jsonInput, @"\\(?![\""\\\/bfnrtu])", "");

            Log.Debug("Cleaned jsonInput: {Json}", jsonInput);

            return System.Text.Json.JsonSerializer.Deserialize<StructuredResponse>(jsonInput);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to deserialize structured response: {Error}", ex.Message);
            return new StructuredResponse(["Error: Invalid JSON format in response."], "Error: Invalid JSON format in response.", 0.0);
        }
    }
}