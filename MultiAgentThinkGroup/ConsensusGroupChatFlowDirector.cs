using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// FIXED: Complete, compiling implementation matching your GroupChatManager definition
internal class ConsensusGroupChatFlowDirector : GroupChatManager
{
    private int _invocationCount;
    private readonly string[] _agentOrder = { "GrokAgent", "ChatGPTAgent", "GeminiAgent" };  // Sequential order

    public ConsensusGroupChatFlowDirector(int maxIterations = 18)
    {
        MaximumInvocationCount = maxIterations;
    }

    /// <summary>
    /// No user input needed mid-chat.
    /// </summary>
    public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(ChatHistory history, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocationCount);
        return ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
        {
            Reason = "No user input required during agent evaluation."
        });
    }

    /// <summary>
    /// Terminates if last 3 evaluation messages all contain 'TERMINATE' or max invocations reached.
    /// </summary>
    public override ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(ChatHistory history, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocationCount);

        // Base max check
        if (InvocationCount > MaximumInvocationCount)
        {
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(true)
            {
                Reason = "Maximum number of invocations reached."
            });
        }

        // Need at least initials + 3 evals
        if (history.Count < 6)
        {
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
            {
                Reason = "Insufficient evaluation turns for consensus."
            });
        }

        // Extract last 3 *evaluation* assistant messages (exclude initials)
        var recentEvals = history
            .Where(m => m.Role == AuthorRole.Assistant &&
                        !m.Content.StartsWith("Grok Initial:") &&
                        !m.Content.StartsWith("ChatGPT Initial:") &&
                        !m.Content.StartsWith("Gemini Initial:"))
            .TakeLast(3)
            .ToList();

        if (recentEvals.Count < 3)
        {
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
            {
                Reason = "Need 3 full evaluation turns."
            });
        }

        bool allTerminate = recentEvals.All(m => m.Content.Contains("TERMINATE"));
        bool shouldTerminate = allTerminate;

        return ValueTask.FromResult(new GroupChatManagerResult<bool>(shouldTerminate)
        {
            Reason = shouldTerminate ? "All agents agreed with TERMINATE." : "Continuing evaluation."
        });
    }

    /// <summary>
    /// Selects next agent sequentially (Grok → ChatGPT → Gemini → repeat).
    /// </summary>
    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(ChatHistory history, GroupChatTeam team, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocationCount);

        // Find last agent who spoke (or default to Grok)
        var lastAgent = history.LastOrDefault(m => m.Role == AuthorRole.Assistant && !string.IsNullOrEmpty(m.AuthorName))?.AuthorName ?? "";
        var lastIndex = Array.IndexOf(_agentOrder, lastAgent);

        // Cycle to next
        int nextIndex = lastIndex >= 0 ? (lastIndex + 1) % _agentOrder.Length : 0;
        string nextAgent = _agentOrder[nextIndex];

        return ValueTask.FromResult(new GroupChatManagerResult<string>(nextAgent)
        {
            Reason = $"Sequential turn: {lastAgent} → {nextAgent}"
        });
    }

    /// <summary>
    /// Filters to a simple consensus summary string.
    /// </summary>
    public override ValueTask<GroupChatManagerResult<string>> FilterResults(ChatHistory history, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocationCount);

        // Basic string summary (enhance with agent call if needed)
        string summary = "Group consensus reached on merged output. Full history preserved for final consolidation.";

        return ValueTask.FromResult(new GroupChatManagerResult<string>(summary)
        {
            Reason = "Filtered to consensus summary."
        });
    }

    /// <summary>
    /// Expose invocation count for base compatibility.
    /// </summary>
    public new int InvocationCount => _invocationCount;
}