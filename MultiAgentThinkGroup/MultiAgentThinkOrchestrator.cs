using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

public class MultiAgentThinkOrchestrator
{
    /// <summary>
    /// Runs the full multi-agent reasoning pipeline using any number of kernels.
    /// The first kernel in the list is used as the final synthesizer/judge.
    /// Model names are derived from the chat completion service's Attributes["Model"] or indexed if not found.
    /// </summary>
    /// <param name="query">The user's query.</param>
    /// <param name="kernels">List of kernels (at least one required).</param>
    /// <returns>The final consolidated answer.</returns>
    public async Task<string?> RunInferenceAsync(string query, IReadOnlyList<Kernel> kernels)
    {
        if (kernels == null || kernels.Count == 0)
            throw new ArgumentException("At least one kernel is required.", nameof(kernels));

        var modelNames = kernels.Select((k, i) =>
        {
            var chatService = k.GetRequiredService<IChatCompletionService>();
            return chatService.Attributes.TryGetValue("Model", out var modelObj) && modelObj is string modelName
                ? modelName
                : $"Model_{i + 1}";
        }).ToList();

        Log.Information("Starting Multi-Agent Reasoning with {count} models...", kernels.Count);

        // ================================================
        // 1. Generate independent answers in parallel
        // ================================================
        Log.Information("Step 1: Generating {count} independent answers in parallel...", kernels.Count);

        const string initialPrompt = """
            Answer the user's question with clear, step-by-step reasoning steps.
            Show your full reasoning.
            At the end, write "### FINAL ANSWER" followed by your complete, polished response.
            """;

        string[] initialAnswers = await GenerateInitialAnswersAsync(query, kernels, modelNames, initialPrompt);

        // ================================================
        // 2. Each model critiques all others (parallel)
        // ================================================
        Log.Information("\nStep 2: Generating cross-critiques (each critiques all others)...");

        const string critiquePromptTemplate = """
            You are an expert reasoning critic.
            Read the answers below from other models.
            For each:
            • Identify flaws in their reasoning steps (missing steps, weak assumptions, logical gaps)
            • Point out factual errors or oversimplifications
            • Suggest specific improvements
            • Be constructive and precise.

            Format:
            ### Critique of {model}
            - [point 1]
            - [point 2]

            {otherAnswers}
            """;

        string[] critiques = await GenerateCrossCritiques(kernels, modelNames, initialAnswers, critiquePromptTemplate);

        for (int i = 0; i < critiques.Length; i++)
            Log.Information("{name}'s Critiques:\n{content}", modelNames[i], critiques[i]);

        // ================================================
        // 3. Final synthesis using the first kernel as judge
        // ================================================
        Log.Information("\nStep 3: Final synthesis by {judge}...", modelNames[0]);

        //const string synthesisPromptTemplate = """
        //    You are the final synthesis engine.
        //    Combine the best ideas from all models into one superior answer.

        //    Original question: {query}

        //    {answersSection}

        //    {critiquesSection}

        //    Task:
        //    • Use the critiques to fix flaws and fill gaps.
        //    • Preserve the strongest reasoning from each model.
        //    • Produce one final, complete, beautifully formatted answer.
        //    • Use headings, bullets, and tables where helpful.

        //    Begin directly with the final answer.
        //    """;

        const string synthesisPromptTemplate = """
            You are the final synthesis engine. Your job is to produce one superior, coherent answer.

            ORIGINAL QUESTION:
            {query}

            === ALL PROPOSALS ===
            {answers}

            === ALL CRITIQUES ===
            {critiques}

            INSTRUCTIONS:
            • Fix every flaw and hallucination pointed out.
            • Keep only the strongest reasoning steps.
            • Resolve all disagreements with evidence and logic.
            • Never mention model names or the debate process.
            • Use beautiful formatting: headings, bullets, tables, LaTeX when helpful.

            Begin your response directly with the final answer.
            """;


        var finalAnswer = await GenerateSynthesisAsync(
            query,
            kernels,
            modelNames,
            initialAnswers,
            critiques,
            synthesisPromptTemplate);

        Log.Information("FINAL CONSOLIDATED ANSWER:\n\n{content}", finalAnswer);

        return finalAnswer;
    }

    private async Task<string> GenerateSynthesisAsync(
        string query,
        IReadOnlyList<Kernel> kernels,
        IReadOnlyList<string> modelNames,
        string[] initialAnswers,
        string[] critiques,
        string synthesisPromptTemplate)
    {
        // Judge is always the first model in the list — fixed, predictable, intentional
        var judgeKernel = kernels[0];
        var judgeName = modelNames[0];

        //Log.Information("Step 3: Final synthesis by {Judge}...", judgeName);

        // Build the two context blocks
        var answersSection = new StringBuilder();
        var critiquesSection = new StringBuilder();

        for (int i = 0; i < initialAnswers.Length; i++)
        {
            answersSection.AppendLine($"### Proposal from {modelNames[i]}")
                          .AppendLine(initialAnswers[i])
                          .AppendLine();

            critiquesSection.AppendLine($"### Critiques from {modelNames[i]}")
                            .AppendLine(critiques[i])
                            .AppendLine();
        }

        // Inject everything into the external template
        var finalPrompt = synthesisPromptTemplate
            .Replace("{query}", query)
            .Replace("{answers}", answersSection.ToString())
            .Replace("{critiques}", critiquesSection.ToString());

        // Run the judge (non-streaming, same as the rest of your code)
        var history = new ChatHistory();
        history.AddUserMessage(finalPrompt);

        var agent = new ChatCompletionAgent
        {
            Kernel = judgeKernel,
            Name = judgeName
        };

        var responseMessages = new List<ChatMessageContent>();
        await foreach (var message in agent.InvokeAsync(history))
        {
            responseMessages.Add(message);
        }

        var finalAnswer = string.Join("", responseMessages.Select(m => m.Content ?? "")).Trim();

        //Log.Information("FINAL CONSOLIDATED ANSWER:\n\n{content}", finalAnswer);

        return finalAnswer;
    }

    private static async Task<string[]> GenerateCrossCritiques(IReadOnlyList<Kernel> kernels, List<string> modelNames, string[] initialAnswers, string critiquePromptTemplate)
    {
        var critiqueTasks = kernels.Select(async (criticKernel, criticIdx) =>
        {
            var sb = new StringBuilder();
            for (int targetIdx = 0; targetIdx < kernels.Count; targetIdx++)
            {
                if (targetIdx == criticIdx) continue;
                sb.AppendLine($"Answer from {modelNames[targetIdx]}:\n{initialAnswers[targetIdx]}");
            }

            var prompt = critiquePromptTemplate.Replace("{otherAnswers}", sb.ToString());

            var history = new ChatHistory();
            history.AddUserMessage(prompt);

            return await InvokeAgentAsync(criticKernel, modelNames[criticIdx], history);
        }).ToArray();

        var critiques = await Task.WhenAll(critiqueTasks);
        return critiques;
    }

    private static async Task<string[]> GenerateInitialAnswersAsync(string query, IReadOnlyList<Kernel> kernels, List<string> modelNames, string initialPrompt)
    {
        var initialHistory = new ChatHistory();
        initialHistory.AddUserMessage(initialPrompt + "\n\nQuestion: " + query);

        var answerTasks = kernels.Select((kernel, index) =>
            InvokeAgentAsync(kernel, modelNames[index], initialHistory)).ToArray();

        var answers = await Task.WhenAll(answerTasks);

        //for (int i = 0; i < answers.Length; i++)
        //    Log.Information("{name} Answer:\n{content}", modelNames[i], answers[i]);
        return answers;
    }

    // Helper: Run one agent
    private static async Task<string> InvokeAgentAsync(Kernel kernel, string name, ChatHistory history)
    {
        var agent = new ChatCompletionAgent { Kernel = kernel, Name = name };
        var result = new List<ChatMessageContent>();
        await foreach (var msg in agent.InvokeAsync(history))
            result.Add(msg);
        return result.LastOrDefault()?.Content?.Trim() ?? "";
    }
}