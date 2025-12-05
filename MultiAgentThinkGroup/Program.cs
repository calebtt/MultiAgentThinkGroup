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

public static partial class Algos
{
    public static void AddConsoleLogger(string? logName)
    {
        var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logName ?? "log.txt",
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 100 * 1024 * 1024,
                retainedFileCountLimit: 5)
            .CreateLogger();

        Log.Logger = serilogLogger;
        Log.Information("Serilog configured.");
    }
}

class Program
{
    private static readonly string openAIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
    private static readonly string googleKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? throw new InvalidOperationException("GOOGLE_API_KEY not set.");
    private static readonly string grokKey = Environment.GetEnvironmentVariable("GROK_API_KEY") ?? throw new InvalidOperationException("GROK_API_KEY not set.");

    private static readonly string openAIModel = "gpt-5.1";
    private static readonly string googleModel = "gemini-3-pro-preview";
    private static readonly string grokModel = "grok-4-1-fast-non-reasoning";

    static async Task Main(string[] args)
    {
        Algos.AddConsoleLogger("MultiAgentThinkGroupLog.txt");

        var orchestrator = new MultiAgentThinkOrchestrator();

        var grokBuilder = Kernel.CreateBuilder();
        grokBuilder.Services.AddSingleton<IChatCompletionService>(new GrokCompletionService(grokKey, grokModel));
        var grokKernel = grokBuilder.Build();

        var chatGPTKernel = Kernel.CreateBuilder().AddOpenAIChatCompletion(openAIModel, openAIKey).Build();
        var geminiKernel = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion(googleModel, googleKey).Build();

        var kernels = new List<Kernel> { grokKernel, chatGPTKernel, geminiKernel };  // First = judge

        var query = "How can I design a thinking model comprised of the top LLM API providers' models? " +
            "I mean Grok, ChatGPT and Gemini for the APIs. " +
            "I have no control over the enterprise models' design or function, but still want to combine them into a better model.";

        var finalAnswer = await orchestrator.RunInferenceAsync(query, kernels);

        Log.Information("FINAL CONSOLIDATED ANSWER:\n\n{content}", finalAnswer);
    }
}