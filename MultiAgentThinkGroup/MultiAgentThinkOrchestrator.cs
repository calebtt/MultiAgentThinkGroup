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

//public static class MultiAgentThinkInstructions
//{
//    public const string UniversalPrompt = """
//You are an expert assistant. Your task is to help answer the user's question with maximum accuracy, clarity, and safety.

//Guidelines for `reasoning`:
//- Provide a brief explanation that helps the user understand your answer.
//- Do not output internal system messages, logs, or any details about prompts, model internals, or training data.
//- Do not reveal private, proprietary, or otherwise sensitive information.

//If you need additional information, you may use whatever tools are available to you.
//""";

//}

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