using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;
using System.Text.Json;

namespace MultiAgentThinkGroup;

/// <summary>
/// Multi-agent conversation (simulated thought) *without* a final judge.
/// Requires initial StructuredResponse objects per agent and runs N rounds of discussion.
/// Produces a transcript only.
/// </summary>
public sealed class MultiAgentConversationSimulatedThoughtOnly
{
    private readonly IReadOnlyList<PanelAgentDescriptor> _agents;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// AgentName, Round, Content
    /// </summary>
    public Func<string, int, string, Task>? TurnLogger { get; set; }

    public MultiAgentConversationSimulatedThoughtOnly(IReadOnlyList<PanelAgentDescriptor> agents)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    }

    /// <summary>
    /// Run multi-agent dialogue for N rounds and return the transcript.
    /// </summary>
    public async Task<List<PanelMessage>> RunAsync(
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

        return transcript;
    }

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
        var replyText = Algos.ExtractText(messages);

        if (TurnLogger is not null)
        {
            await TurnLogger(agent.Name, round, replyText);
        }

        return new PanelMessage(agent.Name, replyText);
    }

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

}
