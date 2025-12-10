// Types.cs
namespace MultiAgentThinkGroup;

using Microsoft.SemanticKernel;

/// <summary>
/// Simple agent descriptor: a name + a Kernel.
/// Use this for panel agents in the multi-agent experiments.
/// </summary>
public sealed record PanelAgentDescriptor(string Name, Kernel Kernel);

/// <summary>
/// Generic transcript entry: which agent said what.
/// </summary>
public sealed record PanelMessage(string Agent, string Content);

public readonly record struct ParsedReasoningStep(string Tag, string Text);

public sealed record ReasoningStats(
    int TotalSteps,
    IReadOnlyDictionary<string, int> TagCounts
);