using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.Grok;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Google;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MultiAgentThinkGroup;

public static partial class Algos
{
    public static StructuredOutputPlugin GetOutputPlugin()
    {
        var outputPlugin = new StructuredOutputPlugin();
        outputPlugin.ResponseSubmitted += (s, e) =>
        {
            // Concatenate Reasoning into a single string
            var reasoningStr = string.Join("\n", e.Response.Reasoning.Select((step) => $"Step: {step}"));

            Log.Information("(StructuredOutputPlugin) Response Submitted! Agent {agentName} structured response submitted:\n\n\nReasoning: {reasoning}\n\n\nSources: {sources}\n\n\n Final: {final}",
                e.AgentName,
                reasoningStr,
                string.Join(", ", e.Response.Sources ?? Array.Empty<string>()),
                e.Response.FinalAnswer);
        };
        return outputPlugin;
    }
}

class Program
{
    private static readonly string openAIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
    private static readonly string googleGeminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? throw new InvalidOperationException("GEMINI_API_KEY not set.");
    private static readonly string googleCustomSearchKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? throw new InvalidOperationException("GOOGLE_CUSTOM_SEARCH_API_KEY not set.");
    private static readonly string googleSearchEngineId = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_ENGINE_ID") ?? throw new InvalidOperationException("GOOGLE_SEARCH_ENGINE_ID not set.");
    private static readonly string grokKey = Environment.GetEnvironmentVariable("GROK_API_KEY") ?? throw new InvalidOperationException("GROK_API_KEY not set.");

    private static readonly string openAIModel = "gpt-5.1";
    private static readonly string googleModel = "gemini-3-pro-preview";
    private static readonly string grokModel = "grok-4-1-fast-non-reasoning";

    private static GoogleConnector googleConnector = new GoogleConnector(apiKey: googleCustomSearchKey, searchEngineId: googleSearchEngineId);

    static async Task Main(string[] args)
    {
        GrokPromptExecutionSettings GetGrokPromptSettings()
        {
            return new GrokPromptExecutionSettings
            {
                ToolCallBehavior = GrokToolCallBehavior.AutoInvokeKernelFunctions,
                StructuredOutputMode = GrokStructuredOutputMode.JsonSchema,
                StructuredOutputSchema = StructuredResponse.GetSchema(),
                ModelId = grokModel
            };
        }

        Algos.AddConsoleLogger("MultiAgentThinkGroupLog.txt");

        var orchestrator = new MultiAgentThinkOrchestrator();

        var grokBuilder = Kernel.CreateBuilder().AddGrokChatCompletion(grokModel, grokKey);
        var chatGPTBuilder = Kernel.CreateBuilder().AddOpenAIChatCompletion(openAIModel, openAIKey);
        var geminiBuilder = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion(googleModel, googleGeminiKey);
        grokBuilder.Services.Configure<GrokPromptExecutionSettings>((x) => GetGrokPromptSettings());
        chatGPTBuilder.Services.Configure<OpenAIPromptExecutionSettings>(s => s.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions);
        geminiBuilder.Services.Configure<GeminiPromptExecutionSettings>(s => s.ToolCallBehavior = GeminiToolCallBehavior.AutoInvokeKernelFunctions);


        var geminiKernel = geminiBuilder.Build();
        var grokKernel = grokBuilder.Build();
        //var grokKernel = Algos.BuildKernel(grokKey, grokModel, "https://api.x.ai/v1");3
        var chatGPTKernel = chatGPTBuilder.Build();


        // Create a web search engine plugin, add to kernels.
        //AddGoogleSearchPlugin(geminiKernel, grokKernel, chatGPTKernel);

        // Also add structured output plugin
        //var outputPlugin = Algos.GetOutputPlugin();

        //var structuredPlugin = KernelPluginFactory.CreateFromObject(outputPlugin, "structured_output");
        //Log.Information("{number} functions in structured output plugin.", structuredPlugin.FunctionCount);

        //grokKernel.Plugins.Add(structuredPlugin);
        //chatGPTKernel.Plugins.Add(structuredPlugin);
        //geminiKernel.Plugins.Add(structuredPlugin);

        var kernels = new List<Kernel> { grokKernel, chatGPTKernel, geminiKernel };  // First = judge

        var query = "How can I build my own motorcycle?";

        //await RunOrchestration(orchestrator, kernels, query);

        //var chatOpts = (0.7f, 400, MultiAgentThinkInstructions.UniversalPrompt);
        //var chat = new LlmChat(chatOpts, outputPlugin, chatGPTKernel);
        //var response = await chat.ProcessUserQueryAsync(query);

        //Log.Information("Response from LlmChat:\n\n{content}", response);

        await SingleKernelTest(orchestrator, grokKernel, "Grok", query);
        //await SingleKernelTest(orchestrator, chatGPTKernel, "ChatGPT", query, 1);
        //await SingleKernelTest(orchestrator, geminiKernel, "Gemini", query, 2);

        //Log.Information("FINAL CONSOLIDATED ANSWER:\n\n{content}", finalAnswer);
    }

    private static void AddGoogleSearchPlugin(Kernel geminiKernel, Kernel grokKernel, Kernel chatGPTKernel)
    {
        var google = new WebSearchEnginePlugin(googleConnector);
        grokKernel.Plugins.AddFromObject(google, "google");
        chatGPTKernel.Plugins.AddFromObject(google, "google");
        geminiKernel.Plugins.AddFromObject(google, "google");
    }

    private static async Task RunOrchestration(MultiAgentThinkOrchestrator orchestrator, List<Kernel> kernels, string query)
    {
        // Use RunWithLiveUIAsync with console logging callback
        //await orchestrator.RunWithLiveUIAsync(query, kernels, update =>
        //{
        //    // Print progress to console
        //    Log.Information("--- Live Update ---");
        //    Log.Information($"Grok: Thought = {update.Grok.Thought}, Action = {update.Grok.Action}, Observation = {update.Grok.Observation}");
        //    Log.Information($"GPT: Thought = {update.Gpt.Thought}, Action = {update.Gpt.Action}, Observation = {update.Gpt.Observation}");
        //    Log.Information($"Gemini: Thought = {update.Gemini.Thought}, Action = {update.Gemini.Action}, Observation = {update.Gemini.Observation}");
        //    if (update.Final != null)
        //    {
        //        Log.Information($"Final: {update.Final.FinalAnswer} (Confidence: {update.Final.Confidence})");
        //    }
        //    Log.Information("-------------------");
        //});
    }

    public static async Task SingleKernelTest(MultiAgentThinkOrchestrator orchestrator, Kernel kernel, string name, string query)
    {
        // Helper: reusable agent factory
        static ChatCompletionAgent CreateAgent(Kernel kernel) => new()
        {
            Kernel = kernel,
            Instructions = "Answer the user.",
            Arguments = new(new GrokPromptExecutionSettings
            {
                ToolCallBehavior = GrokToolCallBehavior.AutoInvokeKernelFunctions,
                StructuredOutputMode = GrokStructuredOutputMode.JsonSchema,
                StructuredOutputSchema = StructuredResponse.GetSchema()
            })
        };

        var agent = CreateAgent(kernel);

        var history = new ChatHistory();
        history.AddUserMessage(query);
        await foreach (var response in agent.InvokeAsync(history))
        {
            Log.Information("Response from {name} Agent:\n\n{content}", name, response.Message);
        }

    }
}