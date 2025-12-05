using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;

public static partial class Algos
{
    public static void AddConsoleLogger(string? logName) { /* unchanged — perfect */ }
}

class Program
{
    // LLM referee for consensus
    private static readonly KernelFunction ConsensusReferee = KernelFunctionFactory.CreateFromPrompt(
        """
        You are an impartial referee.
        Look at the last 3 assistant messages (evaluation phase only).
        Answer with ONLY "true" or "false":

        true  → ALL THREE messages end with "TERMINATE" on its own line
        false → anything else

        History (last 15 messages):
        {{$history}}
        """,
        functionName: "ConsensusReferee");

    // Auto-summarizer
    private static ChatCompletionAgent CreateSummarizer(Kernel kernel) => new()
    {
        Kernel = kernel,
        Name = "Summarizer",
        Instructions = """
            Summarize the conversation so far in under 800 words.
            Preserve:
            • Original user question
            • Each agent's initial key points
            • All agreed improvements and open suggestions
            • Current state of the merged answer
            Use bullet points. Be concise.
            """
    };

    private static async Task KeepHistoryShort(ChatHistory history, Kernel kernel, int maxMessages = 25) 
        => await SummarizeHistoryIfNeeded(CreateSummarizer(kernel), history, maxMessages);

    private static async Task SummarizeHistoryIfNeeded(ChatCompletionAgent summarizer, ChatHistory history, int maxMessages)
    {
        if (history.Count <= maxMessages) return;

        Log.Information("History too long ({count} messages) — summarizing...", history.Count);

        var oldCount = history.Count;
        var toSummarize = history.Take(history.Count - 10).ToList();
        var summaryHistory = new ChatHistory();
        foreach (var m in toSummarize) summaryHistory.Add(m);

        var result = new List<ChatMessageContent>();
        await foreach (var msg in summarizer.InvokeAsync(summaryHistory))
            result.Add(msg);

        var summary = result.LastOrDefault()?.Content ?? "[Summary failed]";

        var recent = history.TakeLast(10).ToList();
        history.Clear();
        history.Add(new ChatMessageContent(AuthorRole.Assistant, $"[SUMMARY OF {oldCount - 10}+ MESSAGES]\n{summary}")
        {
            AuthorName = "Summarizer"
        });
        foreach (var m in recent) history.Add(m);

        Log.Information("History reduced to {count} messages.", history.Count);
    }

    public static async Task<string> InvokeAgentMethod(ChatCompletionAgent agent, ChatHistory history)
    {
        var msgs = new List<ChatMessageContent>();
        await foreach (var m in agent.InvokeAsync(history)) msgs.Add(m);
        return msgs.LastOrDefault()?.Content ?? "";
    }

    static async Task Main(string[] args)
    {
        Algos.AddConsoleLogger("MultiAgentThinkGroupLog.txt");

        var openAIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
        var googleKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? throw new InvalidOperationException("GOOGLE_API_KEY not set.");
        var grokKey = Environment.GetEnvironmentVariable("GROK_API_KEY") ?? throw new InvalidOperationException("GROK_API_KEY not set.");

        var grokBuilder = Kernel.CreateBuilder();
        grokBuilder.Services.AddSingleton<IChatCompletionService>(new GrokCompletionService(grokKey));
        var grokKernel = grokBuilder.Build();

        var chatGPTKernel = Kernel.CreateBuilder().AddOpenAIChatCompletion("gpt-5.1", openAIKey).Build();
        var geminiKernel = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion("gemini-3-pro-preview", googleKey).Build();

        // FINAL INSTRUCTIONS — DELTA-ONLY + CoT CRITIQUE
        const string initialInstructions = """
            First, generate your detailed initial response to the query by breaking down your answer into clear, sequential steps.
            Show your full chain of thought.
            Do NOT include the word 'TERMINATE' in this initial response.
            """;

        const string groupInstructions = """
            You are in a multi-agent debate.
            Your goal: reach consensus on the best possible final answer.

            TURN 1–2: Write a full evaluation of the other two responses.
            - Focus on their chain of thought / reasoning steps.
            - Point out any logical flaws, missing steps, or weak assumptions.
            - Suggest concrete improvements.
            - Do NOT rewrite the entire answer yet.

            TURN 3+: Switch to "delta-only" mode.
            - Do NOT repeat the full answer.
            - Only discuss remaining open points or new suggestions.
            - If you agree with the current merged version, say:
              "I agree with the current merged answer. No further changes needed. TERMINATE"
            - If you have one or two small fixes, list them in bullets, then "TERMINATE".

            Always end with 'TERMINATE' on its own line when you believe the answer is final.
            """;

        var grokAgent = new ChatCompletionAgent { Kernel = grokKernel, Name = "GrokAgent", Instructions = initialInstructions + " " + groupInstructions };
        var chatGPTAgent = new ChatCompletionAgent { Kernel = chatGPTKernel, Name = "ChatGPTAgent", Instructions = initialInstructions + " " + groupInstructions };
        var geminiAgent = new ChatCompletionAgent { Kernel = geminiKernel, Name = "GeminiAgent", Instructions = initialInstructions + " " + groupInstructions };

        var consolidatorAgent = new ChatCompletionAgent
        {
            Kernel = grokKernel,
            Name = "Consolidator",
            Instructions = """
                You are the final consolidator.
                Review the entire group chat history.
                Produce ONE complete, polished, beautifully formatted final answer that combines the best ideas from all agents.
                Use clear headings, bullet points, and tables.
                Keep it under 2500 words.
                Start directly with the answer — no intro.
                """
        };

        var query = "How can I design a thinking model comprised of the top LLM API providers' models? I mean Grok, ChatGPT and Gemini for the APIs." +
    " I have no control over the enterprise models' design or function, but still want to combine them into a better model.";

        // Parallel initials
        var initialHistory = new ChatHistory();
        initialHistory.AddUserMessage(query);

        var tasks = new[]
        {
            Task.Run(() => InvokeAgentMethod(grokAgent, initialHistory)),
            Task.Run(() => InvokeAgentMethod(chatGPTAgent, initialHistory)),
            Task.Run(() => InvokeAgentMethod(geminiAgent, initialHistory))
        };
        var results = await Task.WhenAll(tasks);
        var (grokInit, chatGPTInit, geminiInit) = (results[0], results[1], results[2]);

        Log.Information($"Grok Initial:\n{grokInit}");
        Log.Information($"ChatGPT Initial:\n{chatGPTInit}");
        Log.Information($"Gemini Initial:\n{geminiInit}");

        // MAIN LOOP — DELTA-ONLY + CoT CRITIQUE + AUTO-SUMMARIZE
        var groupHistory = new ChatHistory();
        groupHistory.AddUserMessage(query);
        groupHistory.Add(new ChatMessageContent(AuthorRole.Assistant, $"Grok Initial:\n{grokInit}") { AuthorName = "GrokAgent" });
        groupHistory.Add(new ChatMessageContent(AuthorRole.Assistant, $"ChatGPT Initial:\n{chatGPTInit}") { AuthorName = "ChatGPTAgent" });
        groupHistory.Add(new ChatMessageContent(AuthorRole.Assistant, $"Gemini Initial:\n{geminiInit}") { AuthorName = "GeminiAgent" });
        groupHistory.AddUserMessage("""
            Begin the collaborative evaluation and refinement phase.
            Take turns in order: GrokAgent → ChatGPTAgent → GeminiAgent.

            TURN 1–2: Full evaluation of others' chain of thought and suggestions.
            TURN 3+: Delta-only mode — only discuss remaining improvements.
            When consensus is reached, end with 'TERMINATE' on its own line.
            """);

        var agents = new[] { grokAgent, chatGPTAgent, geminiAgent };
        int turn = 0;
        var summarizer = CreateSummarizer(grokKernel);

        while (turn < 20)
        {
            foreach (var agent in agents)
            {
                turn++;
                Log.Information($"Turn {turn} - {agent.Name} is thinking...");

                // Keep history short — prevents token overflow
                await KeepHistoryShort(groupHistory, grokKernel, maxMessages: 28);

                var turnHistory = new ChatHistory(groupHistory);
                var responseMessages = new List<ChatMessageContent>();

                await foreach (var msg in agent.InvokeAsync(turnHistory))
                    responseMessages.Add(msg);

                var response = responseMessages.LastOrDefault()?.Content ?? "";
                Log.Information($"Turn {turn} - {agent.Name}:\n{response.Trim()}");

                groupHistory.Add(new ChatMessageContent(AuthorRole.Assistant, response) { AuthorName = agent.Name });

                // LLM referee checks consensus
                var context = string.Join("\n\n",
                    groupHistory.TakeLast(15).Select(m => m.AuthorName is null ? $"User: {m.Content}" : $"{m.AuthorName}: {m.Content}"));

                var refereeResult = await ConsensusReferee.InvokeAsync(grokKernel, new() { ["history"] = context });
                bool done = refereeResult.GetValue<string>()?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

                if (done)
                {
                    Log.Information("LLM REFEREE CONFIRMS: CONSENSUS REACHED — TERMINATING");
                    goto Done;
                }
            }
        }

    Done:
        Log.Information("Running Final Consolidator...");
        await KeepHistoryShort(groupHistory, grokKernel, maxMessages: 35);

        var chunks = new List<ChatMessageContent>();
        await foreach (var msg in consolidatorAgent.InvokeAsync(groupHistory))
            chunks.Add(msg);

        var finalAnswer = chunks.LastOrDefault()?.Content?.Trim() ?? "[No output]";

        Log.Information("FINAL CONSOLIDATED ANSWER:\n\n{content}", finalAnswer);
    }
}