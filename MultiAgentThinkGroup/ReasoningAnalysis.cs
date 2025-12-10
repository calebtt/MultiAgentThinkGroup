using System.Text.RegularExpressions;

namespace MultiAgentThinkGroup;


public sealed class ReasoningAnalysis
{
    private static readonly Regex TagRegex = new(
        @"^\s*\[(?<tag>[A-Z_]+)\]\s*(?<text>.*)$",
        RegexOptions.Compiled);

    public IReadOnlyList<ParsedReasoningStep> Parse(StructuredResponse response)
    {
        var list = new List<ParsedReasoningStep>(response.Reasoning.Count);

        foreach (var line in response.Reasoning)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = TagRegex.Match(line);
            if (match.Success)
            {
                var tag = match.Groups["tag"].Value;
                var text = match.Groups["text"].Value;
                list.Add(new ParsedReasoningStep(tag, text));
            }
            else
            {
                list.Add(new ParsedReasoningStep("UNKNOWN", line.Trim()));
            }
        }

        return list;
    }

    public ReasoningStats ComputeStats(StructuredResponse response)
    {
        var parsed = Parse(response);
        var tagCounts = parsed
            .GroupBy(p => p.Tag)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new ReasoningStats(parsed.Count, tagCounts);
    }
}
