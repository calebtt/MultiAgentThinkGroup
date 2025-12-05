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
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;

public class MultiAgentThinkOrchestrator
{
    private readonly string openAIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
    private readonly string googleKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? throw new InvalidOperationException("GOOGLE_API_KEY not set.");
    private readonly string grokKey = Environment.GetEnvironmentVariable("GROK_API_KEY") ?? throw new InvalidOperationException("GROK_API_KEY not set.");

    private readonly string openAIModel = "gpt-5.1";
    private readonly string googleModel = "gemini-3-pro-preview";
    private readonly string grokModel = "grok-4-1-fast-non-reasoning";

    public async Task<string?> RunInferenceAsync(string query)
    {
        var grokBuilder = Kernel.CreateBuilder();
        grokBuilder.Services.AddSingleton<IChatCompletionService>(new GrokCompletionService(grokKey, grokModel));
        var grokKernel = grokBuilder.Build();

        var chatGPTKernel = Kernel.CreateBuilder().AddOpenAIChatCompletion(openAIModel, openAIKey).Build();
        var geminiKernel = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion(googleModel, googleKey).Build();

        // ================================================
        // 1. Generate 3 independent answers in parallel
        // ================================================
        Log.Information("Step 1: Generating 3 independent answers in parallel...");

        var initialPrompt = """
            Answer the user's question with clear, step-by-step reasoning.
            Show your full chain of thought.
            At the end, write "### FINAL ANSWER" followed by your complete, polished response.
            """;

        var initialHistory = new ChatHistory();
        initialHistory.AddUserMessage(initialPrompt + "\n\nQuestion: " + query);

        var grokTask = InvokeAgentAsync(grokKernel, "Grok", initialHistory);
        var chatGPTTask = InvokeAgentAsync(chatGPTKernel, "ChatGPT", initialHistory);
        var geminiTask = InvokeAgentAsync(geminiKernel, "Gemini", initialHistory);

        // Fixed: Wait for all three and extract results properly
        var results = await Task.WhenAll(grokTask, chatGPTTask, geminiTask);
        var grokAnswer = results[0];
        var chatGPTAnswer = results[1];
        var geminiAnswer = results[2];

        Log.Information("Grok Answer:\n{grok}", grokAnswer);
        Log.Information("ChatGPT Answer:\n{chatgpt}", chatGPTAnswer);
        Log.Information("Gemini Answer:\n{gemini}", geminiAnswer);

        // ================================================
        // 2. Each model critiques the other two (parallel)
        // ================================================
        Log.Information("\nStep 2: Each model critiques the other two answers (parallel)...");

        var critiquePrompt = """
            You are an expert reasoning critic.
            Your job: read the two answers below from other models.
            For each:
            • Identify any flaws in their chain of thought (missing steps, weak assumptions, logical gaps)
            • Point out factual errors or oversimplifications
            • Suggest specific improvements to their reasoning or final answer
            • Be constructive and precise.

            Format your response as:
            ### Critique of [Model Name]
            - [point]
            - [point]

            Answer 1 ({model1}):
            {answer1}

            Answer 2 ({model2}):
            {answer2}
            """;

        var critiqueTasks = new[]
        {
            InvokeCritiqueAsync(grokKernel,    "Grok",    chatGPTAnswer, geminiAnswer, "ChatGPT", "Gemini"),
            InvokeCritiqueAsync(chatGPTKernel, "ChatGPT", grokAnswer,    geminiAnswer, "Grok",    "Gemini"),
            InvokeCritiqueAsync(geminiKernel,  "Gemini",  grokAnswer,    chatGPTAnswer, "Grok",    "ChatGPT")
        };

        var critiques = await Task.WhenAll(critiqueTasks);

        Log.Information("Grok's Critique:\n{c1}", critiques[0]);
        Log.Information("ChatGPT's Critique:\n{c2}", critiques[1]);
        Log.Information("Gemini's Critique:\n{c3}", critiques[2]);

        // ================================================
        // 3. Final synthesis by Grok (or any model you prefer)
        // ================================================
        Log.Information("\nStep 3: Final synthesis...");

        var synthesisPrompt = new ChatHistory();
        synthesisPrompt.AddUserMessage($"""
            You are the final synthesis engine.
            Combine the best ideas from all three models into one superior answer.

            Original question: {query}

            Answer A (Grok):
            {grokAnswer}

            Answer B (ChatGPT):
            {chatGPTAnswer}

            Answer C (Gemini):
            {geminiAnswer}

            Critique A (from Grok):
            {critiques[0]}

            Critique B (from ChatGPT):
            {critiques[1]}

            Critique C (from Gemini):
            {critiques[2]}

            Task:
            • Use the critiques to fix flaws and fill gaps.
            • Preserve the strongest reasoning from each model.
            • Produce one final, complete, beautifully formatted answer.
            • Use headings, bullets, and tables where helpful.

            Begin directly with the final answer.
            """);

        var finalChunks = new List<ChatMessageContent>();
        await foreach (var msg in new ChatCompletionAgent { Kernel = grokKernel }.InvokeAsync(synthesisPrompt))
            finalChunks.Add(msg);

        var finalAnswer = finalChunks.LastOrDefault()?.Content?.Trim() ?? "[No output]";

        //Log.Information("FINAL CONSOLIDATED ANSWER:\n\n{content}", finalAnswer);
        return finalAnswer;
    }

    // Helper: Run one agent
    private static async Task<string> InvokeAgentAsync(Kernel kernel, string name, ChatHistory history)
    {
        var agent = new ChatCompletionAgent { Kernel = kernel, Name = name };
        var result = new List<ChatMessageContent>();
        await foreach (var msg in agent.InvokeAsync(history))
            result.Add(msg);
        return result.LastOrDefault()?.Content ?? "";
    }

    // Helper: Run critique
    private static async Task<string> InvokeCritiqueAsync(Kernel kernel, string criticName,
        string answer1, string answer2, string model1, string model2)
    {
        var prompt = $"""
            You are an expert reasoning critic.
            Your job: read the two answers below from other models.
            For each:
            • Identify any flaws in their chain of thought
            • Point out factual errors or oversimplifications
            • Suggest specific improvements
            • Be constructive.

            Format:
            ### Critique of {model1}
            - [point]

            ### Critique of {model2}
            - [point]

            Answer 1 ({model1}):
            {answer1}

            Answer 2 ({model2}):
            {answer2}
            """;

        var history = new ChatHistory();
        history.AddUserMessage(prompt);

        var agent = new ChatCompletionAgent { Kernel = kernel };
        var result = new List<ChatMessageContent>();
        await foreach (var msg in agent.InvokeAsync(history))
            result.Add(msg);

        return result.LastOrDefault()?.Content ?? "";
    }
}