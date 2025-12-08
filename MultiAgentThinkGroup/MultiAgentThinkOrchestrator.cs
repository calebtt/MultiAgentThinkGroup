using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Serilog;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiAgentThinkGroup;

public readonly record struct PanelMessage(string Agent, string Content);

/// <summary>
/// Core multi-agent reasoning engine. No UI, no logging, no callbacks.
/// Returns only the final <see cref="StructuredResponse"/>.
/// </summary>
public sealed class MultiAgentThinkOrchestrator
{
    public static async Task<StructuredResponse> InvokeForStructuredResponseAsync(
    ChatCompletionAgent agent,
    string systemPrompt,
    string userPrompt)
    {
        var history = new ChatHistory();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            history.AddSystemMessage(systemPrompt);
        }

        history.AddUserMessage(userPrompt);

        await foreach (var response in agent.InvokeAsync(history))
        {
            if (response is not AgentResponseItem<ChatMessageContent> responseItem)
            {
                continue;
            }

            var msg = responseItem.Message;
            if (msg is null)
            {
                continue;
            }

            // 1) Try Content
            string? text = msg.Content;

            // 2) If Content is empty, try concatenating TextContent items
            if (string.IsNullOrWhiteSpace(text) && msg.Items is { Count: > 0 })
            {
                var sb = new StringBuilder();
                foreach (var item in msg.Items)
                {
                    if (item is TextContent t && !string.IsNullOrEmpty(t.Text))
                    {
                        sb.Append(t.Text);
                    }
                }

                if (sb.Length > 0)
                {
                    text = sb.ToString();
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // 3) If the text isn't a clean JSON object, try to extract one
            var json = text.Trim();
            if (!json.StartsWith("{"))
            {
                json = ExtractJsonObject(json) ?? json;
            }

            try
            {
                var result = JsonSerializer.Deserialize<StructuredResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });

                if (result is not null)
                {
                    return result;
                }
            }
            catch
            {
                // Ignore and keep looping in case there are later messages
            }
        }

        throw new InvalidOperationException("Agent did not return a valid StructuredResponse JSON.");
    }


    public static async Task<StructuredResponse> RunSingleJudgeCrossAnalysisAsync(
        ChatCompletionAgent grokAgent,
        string question,
        IReadOnlyDictionary<string, StructuredResponse> candidateResponses,
        IReadOnlyList<PanelMessage>? transcript = null)
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        var sb = new StringBuilder();
        sb.AppendLine("Original user question:");
        sb.AppendLine(question);
        sb.AppendLine();

        sb.AppendLine("Candidate StructuredResponses (JSON):");
        foreach (var kvp in candidateResponses)
        {
            sb.AppendLine($"=== {kvp.Key} ===");
            sb.AppendLine(JsonSerializer.Serialize(kvp.Value, jsonOptions));
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

        var userPrompt = sb.ToString();

        // Reuse your existing helper that enforces the StructuredResponse schema
        var combined = await MultiAgentThinkOrchestrator.InvokeForStructuredResponseAsync(
            grokAgent,
            Prompts.CrossAnalysisJudgePrompt,
            userPrompt);

        Log.Information("=== Grok judge combined StructuredResponse ===\n{combined}", combined.ToString());

        return combined;
    }

    // Simple JSON extractor (same logic you use in MultiAgentThinkOrchestrator)
    private static string? ExtractJsonObject(string text)
    {
        var stack = new Stack<char>();
        int start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '{')
            {
                if (stack.Count == 0) start = i;
                stack.Push(c);
            }
            else if (c == '}')
            {
                if (stack.Count == 1 && start != -1)
                {
                    return text.Substring(start, i - start + 1);
                }
                if (stack.Count > 0) stack.Pop();
            }
        }

        return null;
    }


}