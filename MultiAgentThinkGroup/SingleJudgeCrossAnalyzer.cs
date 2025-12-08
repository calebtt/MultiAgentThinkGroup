// SingleJudgeCrossAnalyzer.cs
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Serilog;
using System.Text;
using System.Text.Json;

namespace MultiAgentThinkGroup.Analysis;

public readonly record struct PanelMessage(string Agent, string Content);

/// <summary>
/// Single-responsibility:
/// Given a question + candidate StructuredResponses (+ optional transcript),
/// use a single judge agent to produce one merged StructuredResponse.
/// </summary>
public sealed class SingleJudgeCrossAnalyzer
{
    private readonly ChatCompletionAgent _judgeAgent;
    private readonly JsonSerializerOptions _jsonOptions;

    public SingleJudgeCrossAnalyzer(ChatCompletionAgent judgeAgent)
    {
        _judgeAgent = judgeAgent ?? throw new ArgumentNullException(nameof(judgeAgent));
        _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    }

    /// <summary>
    /// Judge+merge candidate StructuredResponses into a single best StructuredResponse.
    /// </summary>
    public async Task<StructuredResponse> JudgeAsync(
        string question,
        IReadOnlyDictionary<string, StructuredResponse> candidates,
        IReadOnlyList<PanelMessage>? transcript = null)
    {
        var systemPrompt = Prompts.CrossAnalysisJudgePrompt;
        var userPrompt = BuildUserPrompt(question, candidates, transcript);

        var combined = await MultiAgentThinkOrchestrator.InvokeForStructuredResponseAsync(
            _judgeAgent,
            systemPrompt,
            userPrompt);

        LogCombined(combined);

        return combined;
    }

    /// <summary>
    /// Build user prompt text summarizing question, candidates, and transcript.
    /// </summary>
    private string BuildUserPrompt(
        string question,
        IReadOnlyDictionary<string, StructuredResponse> candidates,
        IReadOnlyList<PanelMessage>? transcript)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Original user question:");
        sb.AppendLine(question);
        sb.AppendLine();

        sb.AppendLine("Candidate StructuredResponses (JSON):");
        foreach (var kvp in candidates)
        {
            sb.AppendLine($"=== {kvp.Key} ===");
            sb.AppendLine(JsonSerializer.Serialize(kvp.Value, _jsonOptions));
            sb.AppendLine();
        }

        if (transcript is not null && transcript.Count > 0)
        {
            sb.AppendLine("Panel discussion between agents (for your context):");
            foreach (var msg in transcript)
            {
                sb.AppendLine($"[{msg.Agent}] {msg.Content}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Using the question, the candidate StructuredResponses, and (if present) the discussion above,");
        sb.AppendLine("produce a single best StructuredResponse as specified in your system instructions.");

        return sb.ToString();
    }

    /// <summary>
    /// Logging & diagnostics for the combined response.
    /// </summary>
    private void LogCombined(StructuredResponse combined)
    {
        Log.Information("=== Judge combined StructuredResponse ===\n{combined}", combined.ToString());
    }
}
