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

class Program
{
    static async Task Main(string[] args)
    {
        // Load keys
        var openAIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
        var googleKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? throw new InvalidOperationException("GOOGLE_API_KEY not set.");

        // Kernels for each LLM
        var grokBuilder = Kernel.CreateBuilder();
        grokBuilder.Services.AddSingleton<IChatCompletionService>(new GrokCompletionService());
        var grokKernel = grokBuilder.Build();

        var chatGPTKernel = Kernel.CreateBuilder().AddOpenAIChatCompletion("gpt-5.1", openAIKey).Build();
        var geminiKernel = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion("gemini-3-pro-preview", googleKey).Build();

        // Agents with tweaked instructions: No simulation of others, explicit multi-turn, terminate only on group consensus
        var grokAgent = new ChatCompletionAgent
        {
            Kernel = grokKernel,
            Name = "GrokAgent",
            Instructions = "First, generate your initial response to the query with step-by-step Chain of Thought (CoT) reasoning. In group chat, respond only on your turn. Evaluate others' responses (quality 1-10, veracity 1-10, suggestions). Analyze CoT steps for validity; if discrepancies exist, provide rationale and propose resolution toward consensus. Propose refinements. Do not simulate others. End ONLY when all agree on consensus with 'TERMINATE'."
        };

        var chatGPTAgent = new ChatCompletionAgent
        {
            Kernel = chatGPTKernel,
            Name = "ChatGPTAgent",
            Instructions = "First, generate your initial response to the query with step-by-step Chain of Thought (CoT) reasoning. In group chat, respond only on your turn. Evaluate others' responses (quality 1-10, veracity 1-10, suggestions). Analyze CoT steps for validity; if discrepancies exist, provide rationale and propose resolution toward consensus. Propose refinements. Do not simulate others. End ONLY when all agree on consensus with 'TERMINATE'."
        };

        var geminiAgent = new ChatCompletionAgent
        {
            Kernel = geminiKernel,
            Name = "GeminiAgent",
            Instructions = "First, generate your initial response to the query with step-by-step Chain of Thought (CoT) reasoning. In group chat, respond only on your turn. Evaluate others' responses (quality 1-10, veracity 1-10, suggestions). Analyze CoT steps for validity; if discrepancies exist, provide rationale and propose resolution toward consensus. Propose refinements. Do not simulate others. End ONLY when all agree on consensus with 'TERMINATE'."
        };

        var consolidatorAgent = new ChatCompletionAgent
        {
            Kernel = grokKernel,
            Name = "Consolidator",
            Instructions = "Review the group chat history, including initial CoT responses and evaluations. Highlight additional relevant conclusions from debates. Merge into a single, improved output with additional conclusions, resolving conflicts, and citing sources. Use CoT in your merging process."
        };

        // Sample Query (or use a simpler test query like "What is 2+2?" for debugging)
        var query = "What is the impact of AI on climate change?";  // Or test: "What is 2+2? Explain step by step."

        // Generate initial CoT responses from each agent individually
        var initialHistory = new ChatHistory();
        initialHistory.AddUserMessage(query);

        var grokInitialMessages = new List<ChatMessageContent>();
        await foreach (var msg in grokAgent.InvokeAsync(initialHistory))
        {
            grokInitialMessages.Add(msg);
        }
        var grokInitialContent = grokInitialMessages.LastOrDefault()?.Content ?? "";
        Console.WriteLine($"Grok Initial: {grokInitialContent}");

        var chatGPTInitialMessages = new List<ChatMessageContent>();
        await foreach (var msg in chatGPTAgent.InvokeAsync(initialHistory))
        {
            chatGPTInitialMessages.Add(msg);
        }
        var chatGPTInitialContent = chatGPTInitialMessages.LastOrDefault()?.Content ?? "";
        Console.WriteLine($"ChatGPT Initial: {chatGPTInitialContent}");

        var geminiInitialMessages = new List<ChatMessageContent>();
        await foreach (var msg in geminiAgent.InvokeAsync(initialHistory))
        {
            geminiInitialMessages.Add(msg);
        }
        var geminiInitialContent = geminiInitialMessages.LastOrDefault()?.Content ?? "";
        Console.WriteLine($"Gemini Initial: {geminiInitialContent}");

        // Define Termination Function (adjusted to check if ALL recent messages include 'TERMINATE')
        var terminationFunction = KernelFunctionFactory.CreateFromPrompt(
            "Review the last three agent messages: {{$input}}\n" +
            "If ALL contain the word 'TERMINATE', output 'true'; otherwise, output 'false'.",
            functionName: "ShouldTerminate"
        );

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var terminationStrategy = new KernelFunctionTerminationStrategy(terminationFunction, chatGPTKernel)
        {
            MaximumIterations = 15,  // Increased for more rounds (5 rounds x 3 agents)
            Arguments = new KernelArguments { ["input"] = "{{$history.LastN(3)}}" }  // Custom to check last 3
        };
#pragma warning restore SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var selectionStrategy = new SequentialSelectionStrategy();  // Ensures forced sequential turns
#pragma warning restore SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        // Group Chat for Evaluation/Refinement
#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var groupChat = new AgentGroupChat(grokAgent, chatGPTAgent, geminiAgent)
        {
            ExecutionSettings = new AgentGroupChatSettings
            {
                TerminationStrategy = terminationStrategy,
                SelectionStrategy = selectionStrategy
            }
        };
#pragma warning restore SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        // Seed group chat with initial responses
        groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, query));
        groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.Assistant, $"Grok Initial: {grokInitialContent}", grokAgent.Name));
        groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.Assistant, $"ChatGPT Initial: {chatGPTInitialContent}", chatGPTAgent.Name));
        groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.Assistant, $"Gemini Initial: {geminiInitialContent}", geminiAgent.Name));
        groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, "Now evaluate each other's CoT responses, suggest improvements, refine collaboratively, and reach consensus on a merged output. Take turns: Grok first, then ChatGPT, then Gemini. Continue until all agree."));

        // Invoke Group Chat (Iterations) with debug logging
        Console.WriteLine("Group Chat Evaluations:");
        int turn = 0;
        await foreach (var message in groupChat.InvokeAsync())
        {
            turn++;
            Console.WriteLine($"Turn {turn} - {message.AuthorName}: {message.Content}");
        }

        // Retrieve chat history
        var historyMessages = new List<ChatMessageContent>();
        await foreach (var msg in groupChat.GetChatMessagesAsync())
        {
            historyMessages.Add(msg);
        }

        // Check history length and summarize if too long (to avoid truncation)
        if (historyMessages.Count > 50)  // Arbitrary threshold; adjust based on token limits
        {
            var summarizer = new ChatCompletionAgent
            {
                Kernel = grokKernel,
                Name = "Summarizer",
                Instructions = "Summarize the chat history concisely, preserving key CoT, evaluations, and proposals."
            };
            var summaryInput = new ChatHistory(historyMessages);
            var summaryMessages = new List<ChatMessageContent>();
            await foreach (var msg in summarizer.InvokeAsync(summaryInput))
            {
                summaryMessages.Add(msg);
            }
            historyMessages = summaryMessages;  // Replace with summary for consolidator
            Console.WriteLine("History summarized due to length.");
        }

        // Consolidate
        var finalInput = new ChatHistory(historyMessages);
        var finalOutputs = new List<ChatMessageContent>();
        await foreach (var msg in consolidatorAgent.InvokeAsync(finalInput))
        {
            finalOutputs.Add(msg);
        }
        if (finalOutputs.Count > 0)
        {
            Console.WriteLine($"Final Output: {finalOutputs.Last().Content}");
        }
    }
}