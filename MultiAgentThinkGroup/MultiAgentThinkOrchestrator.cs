// MultiAgentThinkOrchestrator.cs
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;
using System.Text.Json;

namespace MultiAgentThinkGroup;

/// <summary>
/// Core helper for invoking a ChatCompletionAgent and deserializing a StructuredResponse.
/// Uses the agent's Instructions as the system prompt.
/// </summary>
public sealed class MultiAgentThinkOrchestrator
{
    public static async Task<StructuredResponse> InvokeForStructuredResponseAsync(
        ChatCompletionAgent agent,
        string userPrompt)
    {
        var history = new ChatHistory();

        // Agent.Instructions is handled by SK internally; we only add the user message here.
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

            string? text = msg.Content;

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

            var json = text.Trim();
            if (!json.StartsWith("{"))
            {
                json = ExtractJsonObject(json) ?? json;
            }

            try
            {
                var result = JsonSerializer.Deserialize<StructuredResponse>(
                    json,
                    new JsonSerializerOptions
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
