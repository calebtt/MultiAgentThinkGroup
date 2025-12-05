using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Google;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


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
        Algos.AddConsoleLogger("MultiAgentThinkGroupLog.txt");

        var orchestrator = new MultiAgentThinkOrchestrator();

        var grokBuilder = Kernel.CreateBuilder();
        grokBuilder.Services.AddSingleton<IChatCompletionService>(new GrokCompletionService(grokKey, grokModel));
        var grokKernel = grokBuilder.Build();
        var chatGPTKernel = Kernel.CreateBuilder().AddOpenAIChatCompletion(openAIModel, openAIKey).Build();
        var geminiKernel = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion(googleModel, googleGeminiKey).Build();  // Correct: model + API key

        // Create a web search engine plugin
        var google = new WebSearchEnginePlugin(googleConnector);
        // add to kernels
        grokKernel.ImportPluginFromObject(google, "google");
        chatGPTKernel.ImportPluginFromObject(google, "google");
        geminiKernel.ImportPluginFromObject(google, "google");

        var kernels = new List<Kernel> { grokKernel, chatGPTKernel, geminiKernel };  // First = judge

        var query = "How can I design a thinking model comprised of the top LLM API providers' models? " +
            "I mean Grok, ChatGPT and Gemini for the APIs. " +
            "I have no control over the enterprise models' design or function, but still want to combine them into a better model.";

        var finalAnswer = await orchestrator.RunInferenceAsync(query, kernels);

        Log.Information("FINAL CONSOLIDATED ANSWER:\n\n{content}", finalAnswer);
    }
}