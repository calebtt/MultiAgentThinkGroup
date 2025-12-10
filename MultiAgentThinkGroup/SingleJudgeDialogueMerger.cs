// SingleJudgeDialogueMerger.cs
namespace MultiAgentThinkGroup;

/// <summary>
/// Multi-agent conversation (simulated thought) followed by a single judge merge.
/// - Requires initial StructuredResponse objects per agent.
/// - Runs N rounds of discussion, then uses a judge to merge.
/// </summary>
public sealed class SingleJudgeDialogueMerger
{
    private readonly SingleJudgeCrossAnalyzer _judge;
    private readonly MultiAgentConversationSimulatedThoughtOnly _conversationalist;

    /// <summary>
    /// Raised after each panel turn: (agent name, round, content).
    /// </summary>
    public event Func<string, int, string, Task>? TurnOccurred;

    /// <summary>
    /// Raised once the judge has produced the merged StructuredResponse.
    /// </summary>
    public event EventHandler<StructuredResponseEventArgs>? JudgeCompleted;

    public SingleJudgeDialogueMerger(
        IReadOnlyList<PanelAgentDescriptor> agents,
        SingleJudgeCrossAnalyzer judge)
    {
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
        _conversationalist = new MultiAgentConversationSimulatedThoughtOnly(agents)
        {
            // Bridge TurnOccurred into the inner conversation engine
            TurnLogger = async (name, round, content) =>
            {
                if (TurnOccurred is not null)
                {
                    await TurnOccurred.Invoke(name, round, content);
                }
            }
        };
    }

    /// <summary>
    /// Run multi-agent dialogue for N rounds, then ask the judge agent to merge.
    /// Returns the transcript + the combined StructuredResponse.
    /// </summary>
    public async Task<(List<PanelMessage> Transcript, StructuredResponse Combined)>
        RunAsync(
            string question,
            IReadOnlyDictionary<string, StructuredResponse> initialResponses,
            int rounds)
    {
        var transcript = await _conversationalist.RunAsync(
            question,
            initialResponses,
            rounds);

        var combined = await _judge.JudgeAsync(question, initialResponses, transcript);

        JudgeCompleted?.Invoke(
            this,
            new StructuredResponseEventArgs("Judge", combined));

        return (transcript, combined);
    }
}
