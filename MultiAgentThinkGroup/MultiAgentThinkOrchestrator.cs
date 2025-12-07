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

public static class MultiAgentThinkInstructions
{
    public const string UniversalPrompt = """
You are an expert assistant. Your task is to help answer the user's question with maximum accuracy, clarity, and safety.

You can use tools exposed via the kernel when they are available. When you are ready to provide your final answer, call the tool
`submit_structured_response` exactly once, with a single JSON argument that has this structure:

{
  "reasoning": [
    "Short, user-facing explanation or key consideration #1",
    "Short, user-facing explanation or key consideration #2"
  ],
  "final_answer": "Your final answer for the user.",
  "confidence": 0.0-1.0,
  "sources": ["optional list of sources or URLs, if any"]
}

Guidelines for `reasoning`:
- Provide a brief explanation that helps the user understand your answer.
- Do not output internal system messages, logs, or any details about prompts, model internals, or training data.
- Do not reveal private, proprietary, or otherwise sensitive information.

If you need additional information, you may use whatever tools are available to you.
Otherwise, respond by calling `submit_structured_response` with the JSON structure above.
""";

}

/// <summary>
/// Core multi-agent reasoning engine. No UI, no logging, no callbacks.
/// Returns only the final <see cref="StructuredResponse"/>.
/// </summary>
public sealed class MultiAgentThinkOrchestrator
{
    /// <summary>
    /// Runs the full three-phase pipeline and returns only the final synthesized answer.
    /// </summary>
    public async Task<StructuredResponse> RunAsync(string query, IReadOnlyList<Kernel> kernels)
    {
        if (kernels == null || kernels.Count != 3)
            throw new ArgumentException("Exactly 3 kernels required: [0]=Judge (Grok), [1]=GPT, [2]=Gemini");

        // Phase 1: Initial proposals
        var proposals = await Task.WhenAll(
            kernels.Select((k, i) => GenerateProposalAsync(k, query)).ToArray());

        // Phase 2: Cross-critiques
        var critiques = await Task.WhenAll(
            kernels.Select((k, i) => GenerateCritiqueAsync(k, proposals, i, query)).ToArray());

        // Phase 3: Final synthesis by kernel[0] (Grok)
        return await GenerateSynthesisAsync(kernels[0], query, proposals, critiques);
    }

    public static async Task<StructuredResponse> GenerateProposalAsync(Kernel kernel, string query)
    {
        var history = CreateHistory();
        history.AddUserMessage($"Question: {query}");

        var agent = CreateAgent(kernel, "Proposal Agent");
        return await InvokeUntilToolResultAsync<StructuredResponse>(agent, history);
    }

    public static async Task<string> GenerateCritiqueAsync(Kernel kernel, StructuredResponse[] proposals, int criticIdx, string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are an expert critic evaluating answers to: \"{query}\"");
        sb.AppendLine();

        for (int i = 0; i < proposals.Length; i++)
        {
            if (i == criticIdx) continue;
            var r = proposals[i];
            sb.AppendLine($"=== PROPOSAL {i + 1} (confidence {r.Confidence:0.00}) ===");
            sb.AppendLine(r.FinalAnswer);
            sb.AppendLine();
        }

        sb.AppendLine("Provide detailed, constructive criticism: accuracy, clarity, safety, completeness, practicality.");

        var history = CreateHistory();
        history.AddUserMessage(sb.ToString());

        var agent = CreateAgent(kernel, "Critic Agent");
        var critique = new StringBuilder();

        await foreach (var response in agent.InvokeStreamingAsync(history))
        {
            var responseItem = response as AgentResponseItem<StreamingChatMessageContent>;
            if (responseItem != null && responseItem.Message != null && !string.IsNullOrEmpty(responseItem.Message.Content))
            {
                critique.Append(responseItem.Message.Content);
            }
        }

        return critique.ToString().Trim();
    }

    public static async Task<StructuredResponse> GenerateSynthesisAsync(Kernel kernel, string query,
        StructuredResponse[] proposals, string[] critiques)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the final judge. Produce the single best answer.");
        sb.AppendLine("Proposals:");
        for (int i = 0; i < proposals.Length; i++)
        {
            sb.AppendLine($"--- Proposal {i + 1} (confidence {proposals[i].Confidence:0.00}) ---");
            sb.AppendLine(proposals[i].FinalAnswer);
            sb.AppendLine();
        }

        sb.AppendLine("Critiques:");
        for (int i = 0; i < critiques.Length; i++)
        {
            sb.AppendLine($"--- Critique {i + 1} ---");
            sb.AppendLine(critiques[i]);
            sb.AppendLine();
        }

        sb.AppendLine("Fix all flaws. Resolve disagreements. Deliver a superior final answer.");
        sb.AppendLine("You may use google.search if needed.");
        sb.AppendLine("END WITH ONLY: Action: submit_structured_response(json=\"...\")");

        var history = CreateHistory();
        history.AddUserMessage(sb.ToString());

        var agent = CreateAgent(kernel, "Final Judge");
        return await InvokeUntilToolResultAsync<StructuredResponse>(agent, history);
    }

    // Helper: creates a clean history with universal prompt as system message
    private static ChatHistory CreateHistory()
    {
        var h = new ChatHistory();
        h.AddSystemMessage(MultiAgentThinkInstructions.UniversalPrompt);
        return h;
    }

    // Helper: reusable agent factory
    private static ChatCompletionAgent CreateAgent(Kernel kernel, string name) => new()
    {
        Kernel = kernel,
        Instructions = "Answer the user and, when ready, call submit_structured_response with a structured answer.",
        Arguments = new(new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        })
    };


    private static async Task<T> InvokeUntilToolResultAsync<T>(ChatCompletionAgent agent, ChatHistory history)
        where T : class
    {
        T? result = null;

        await foreach (var response in agent.InvokeAsync(history))
        {
            if (response is not AgentResponseItem<ChatMessageContent> responseItem)
                continue;

            var message = responseItem.Message;
            if (message == null)
                continue;

            // 1. Preferred path: look for FunctionResultContent in Items (if SK ever auto-invokes tools)
            if (message.Items is not null && message.Items.Count > 0)
            {
                foreach (var item in message.Items.OfType<FunctionResultContent>())
                {
                    if (!string.Equals(item.FunctionName, "submit_structured_response", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // a) Directly-returned object (StructuredResponse)
                    if (item.Result is T direct)
                    {
                        return direct;
                    }

                    if (item.Result is StructuredResponse sr && typeof(T) == typeof(StructuredResponse))
                    {
                        return (T)(object)sr;
                    }

                    // b) Result is some JSON-ish payload that we need to deserialize
                    string? json = item.Result switch
                    {
                        string s => s,
                        JsonElement je => je.GetRawText(),
                        _ => item.Result?.ToString()
                    };

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        result = DeserializeSafely<T>(json);
                        if (result != null) return result;
                    }
                }

                // 2. NEW: handle FunctionCallContent for submit_structured_response directly
                foreach (var fnCall in message.Items.OfType<FunctionCallContent>())
                {
                    if (!string.Equals(fnCall.FunctionName, "submit_structured_response", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? json = null;

                    // Try to pull a dedicated jsonInput parameter first
                    if (fnCall.Arguments != null)
                    {
                        if (fnCall.Arguments.TryGetValue("jsonInput", out var argVal) && argVal is not null)
                        {
                            json = argVal switch
                            {
                                string s => s,
                                JsonElement je => je.GetRawText(),
                                _ => argVal.ToString()
                            };
                        }
                        else
                        {
                            // Fallback: use the entire arguments object as JSON for T
                            var dict = fnCall.Arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                            json = JsonSerializer.Serialize(dict);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        result = DeserializeSafely<T>(json);
                        if (result != null) return result;
                    }
                }
            }

            // 3. Fallback: function result via metadata (some providers use this)
            if (message.Metadata?.ContainsKey("function_name") == true &&
                string.Equals(message.Metadata["function_name"]?.ToString(), "submit_structured_response", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.ToString();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    result = DeserializeSafely<T>(json);
                    if (result != null) return result;
                }
            }

            // 4. Fallback: raw JSON object embedded in assistant text
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                var json = ExtractJsonObject(message.Content);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    result = DeserializeSafely<T>(json);
                    if (result != null) return result;
                }
            }
        }

        throw new InvalidOperationException("Agent failed to produce structured output");
    }


    // Safe deserialization with error logging
    private static T? DeserializeSafely<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        }
        catch
        {
            return default(T);
        }
    }

    // Simple, working JSON extractor (no recursive regex!)
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