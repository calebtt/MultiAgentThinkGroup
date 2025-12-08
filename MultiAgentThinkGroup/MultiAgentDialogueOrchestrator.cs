// MultiAgentDialogueOrchestrator.cs
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;

namespace MultiAgentThinkGroup.Analysis;

public sealed class MultiAgentDialogueOrchestrator
{
    private readonly IReadOnlyList<PanelAgentDescriptor> _agents;
    private readonly SingleJudgeCrossAnalyzer _judge;
    private readonly JsonSerializerOptions _jsonOptions;

    public MultiAgentDialogueOrchestrator(
        IReadOnlyList<PanelAgentDescriptor> agents,
        SingleJudgeCrossAnalyzer judge)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
        _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    }

    /// <summary>
    /// Main entry point: run multi-agent dialogue for N rounds, then ask Grok to merge.
    /// </summary>
    public async Task<(List<PanelMessage> Transcript, StructuredResponse Combined)>
        RunAsync(
            string question,
            IReadOnlyDictionary<string, StructuredResponse> initialResponses,
            int rounds)
    {
        var transcript = new List<PanelMessage>();

        for (int round = 1; round <= rounds; round++)
        {
            foreach (var agent in _agents)
            {
                var reply = await RunSingleTurnAsync(agent, question, initialResponses, transcript, round);
                transcript.Add(reply);
            }
        }

        var combined = await _judge.JudgeAsync(question, initialResponses, transcript);
        return (transcript, combined);
    }

    /// <summary>
    /// Single-responsibility: run one agent's turn and return its message.
    /// </summary>
    private async Task<PanelMessage> RunSingleTurnAsync(
        PanelAgentDescriptor agent,
        string question,
        IReadOnlyDictionary<string, StructuredResponse> initialResponses,
        IReadOnlyList<PanelMessage> transcript,
        int round)
    {
        var chatService = agent.Kernel.GetRequiredService<IChatCompletionService>();

        var systemPrompt = BuildSystemPromptForAgent(agent, _agents);
        var userPrompt = BuildUserPromptForAgent(agent, question, initialResponses, transcript);

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        var messages = await chatService.GetChatMessageContentsAsync(history);
        var replyText = ExtractText(messages);

        LogTurn(agent.Name, round, replyText);

        return new PanelMessage(agent.Name, replyText);
    }

    /// <summary>
    /// Single-responsibility: system prompt for a given panelist.
    /// </summary>
    private string BuildSystemPromptForAgent(
        PanelAgentDescriptor agent,
        IReadOnlyList<PanelAgentDescriptor> allAgents)
    {
        var otherNames = allAgents
            .Where(a => a.Name != agent.Name)
            .Select(a => a.Name)
            .ToArray();

        return Prompts.CrossAnalysisPanelistPrompt
            .Replace("{AGENT_NAME}", agent.Name)
            .Replace("{OTHER_AGENTS}", string.Join(", ", otherNames));
    }

    /// <summary>
    /// Single-responsibility: user prompt with question, own SR, others' SR, and transcript.
    /// </summary>
    private string BuildUserPromptForAgent(
        PanelAgentDescriptor agent,
        string question,
        IReadOnlyDictionary<string, StructuredResponse> initialResponses,
        IReadOnlyList<PanelMessage> transcript)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Original user question:");
        sb.AppendLine(question);
        sb.AppendLine();

        sb.AppendLine("Your own initial StructuredResponse (JSON):");
        if (initialResponses.TryGetValue(agent.Name, out var own))
        {
            sb.AppendLine(JsonSerializer.Serialize(own, _jsonOptions));
        }
        else
        {
            sb.AppendLine("(Not available)");
        }
        sb.AppendLine();

        sb.AppendLine("Other agents' initial StructuredResponses (JSON):");
        foreach (var kvp in initialResponses.Where(kvp => kvp.Key != agent.Name))
        {
            sb.AppendLine($"=== {kvp.Key} ===");
            sb.AppendLine(JsonSerializer.Serialize(kvp.Value, _jsonOptions));
            sb.AppendLine();
        }

        if (transcript.Count > 0)
        {
            sb.AppendLine("Conversation so far:");
            foreach (var msg in transcript)
            {
                sb.AppendLine($"[{msg.Agent}] {msg.Content}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("For this turn:");
        sb.AppendLine("- Briefly analyze where you agree or disagree with the other agents' reasoning.");
        sb.AppendLine("- Call out important assumptions, risks, or conflicts you see.");
        sb.AppendLine("- Suggest concrete improvements or corrections.");
        sb.AppendLine("Respond in plain text (no JSON), in at most 8 short bullet points or paragraphs.");

        return sb.ToString();
    }

    /// <summary>
    /// Single-responsibility: extract plain text from chat completion result.
    /// </summary>
    private static string ExtractText(IReadOnlyList<ChatMessageContent> messages)
    {
        var reply = messages.LastOrDefault();
        if (reply is null) return string.Empty;

        if (!string.IsNullOrWhiteSpace(reply.Content))
        {
            return reply.Content;
        }

        var parts = reply.Items
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Single-responsibility: logging for one turn.
    /// </summary>
    private static void LogTurn(string agentName, int round, string content)
    {
        Log.Information("[Round {round}] {agent}:\n{content}", round, agentName, content);
    }
}

/// <summary>
/// Descriptor for a panel agent: a name + a Kernel (from which we'll get IChatCompletionService).
/// </summary>
public sealed record PanelAgentDescriptor(string Name, Kernel Kernel);
