// MultiAgentThinkOrchestrator.cs
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;
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
        string userPrompt,
        ILogger? logger = null,
        int maxLoggedChars = 6000)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        if (userPrompt is null) throw new ArgumentNullException(nameof(userPrompt));

        logger ??= Log.Logger;

        static string SafeRole(AuthorRole role) => role.ToString();

        string Trunc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", "");
            return s.Length <= maxLoggedChars ? s : s.Substring(0, maxLoggedChars) + "…(truncated)";
        }

        var history = new ChatHistory();

        // Agent.Instructions is handled by SK internally; we only add the user message here.
        history.AddUserMessage(userPrompt);

        int inspected = 0;
        string? lastRawText = null;
        string? lastJsonCandidate = null;
        Exception? lastParseException = null;
        bool sawToolCalls = false;

        logger.Debug("Invoking agent {AgentName}. Prompt chars={PromptLen}", agent.Name ?? "<unnamed>", userPrompt.Length);

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

            inspected++;

            // Detect tool calls (if your agent pipeline supports them)
            if (msg.Items is { Count: > 0 })
            {
                foreach (var item in msg.Items)
                {
                    if (item is FunctionCallContent fc)
                    {
                        sawToolCalls = true;
                        logger.Information(
                            "Agent returned FunctionCallContent (tool call): plugin={Plugin} function={Function} id={Id}",
                            fc.PluginName, fc.FunctionName, fc.Id);
                    }
                }
            }

            // Prefer msg.Content; if empty, rebuild from TextContent items (your original behavior) :contentReference[oaicite:3]{index=3}
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
                logger.Debug("Message #{Index} from agent had no text (Role={Role}).", inspected, SafeRole(msg.Role));
                continue;
            }

            lastRawText = text;

            var json = text.Trim();
            if (!json.StartsWith("{", StringComparison.Ordinal))
            {
                json = ExtractJsonObject(json) ?? json;
            }

            lastJsonCandidate = json;

            logger.Debug(
                "Message #{Index} (Role={Role}) raw:\n{Raw}",
                inspected, SafeRole(msg.Role), Trunc(text));

            logger.Debug(
                "Message #{Index} JSON candidate:\n{JsonCandidate}",
                inspected, Trunc(json));

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
                    logger.Information("Parsed StructuredResponse successfully from message #{Index}.", inspected);
                    return result;
                }

                logger.Warning("Deserialize returned null for message #{Index}.", inspected);
            }
            catch (Exception ex)
            {
                lastParseException = ex;
                logger.Warning(ex, "JSON parse failed on message #{Index}.", inspected);
                // Keep looping in case there are later messages
            }
        }

        // Final throw with actionable diagnostics (instead of the generic message) :contentReference[oaicite:4]{index=4}
        var err = new StringBuilder();
        err.AppendLine("Agent did not return a valid StructuredResponse JSON.");
        err.AppendLine($"Messages inspected: {inspected}");
        err.AppendLine($"Saw tool calls: {sawToolCalls}");
        if (lastParseException is not null)
        {
            err.AppendLine($"Last parse exception: {lastParseException.GetType().Name}: {lastParseException.Message}");
        }
        if (!string.IsNullOrWhiteSpace(lastRawText))
        {
            err.AppendLine("Last raw text:");
            err.AppendLine(Trunc(lastRawText));
        }
        if (!string.IsNullOrWhiteSpace(lastJsonCandidate))
        {
            err.AppendLine("Last JSON candidate:");
            err.AppendLine(Trunc(lastJsonCandidate));
        }

        // Keep the inner exception so you can see it in crash dumps too
        throw new InvalidOperationException(err.ToString(), lastParseException);
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
