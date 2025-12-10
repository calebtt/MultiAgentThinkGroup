using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Grok;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Google;
using MultiAgentThinkGroup;
using Serilog;
using System.Text;
using System.Text.Json;

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

        var grokBuilder = Kernel.CreateBuilder().AddGrokChatCompletion(grokModel, grokKey);
        var chatGPTBuilder = Kernel.CreateBuilder().AddOpenAIChatCompletion(openAIModel, openAIKey);
        var geminiBuilder = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion(googleModel, googleGeminiKey);

        var geminiKernel = geminiBuilder.Build();
        var grokKernel = grokBuilder.Build();
        var chatGPTKernel = chatGPTBuilder.Build();

        // Create a web search engine plugin, add to kernels.
        //AddGoogleSearchPlugin(geminiKernel, grokKernel, chatGPTKernel);

        var query = "How can I build my own motorcycle?";

        // Initial reasoning phase (structured outputs)
        var grokInitial = await MultiAgentThinkOrchestrator
            .InvokeForStructuredResponseAsync(Algos.CreateGrokAgent(grokKernel, Prompts.InitialStepPrompt), query);
        var chatGptInitial = await MultiAgentThinkOrchestrator
            .InvokeForStructuredResponseAsync(Algos.CreateChatGPTAgent(chatGPTKernel, Prompts.InitialStepPrompt), query);
        var geminiInitial = await MultiAgentThinkOrchestrator
            .InvokeForStructuredResponseAsync(Algos.CreateGeminiAgent(geminiKernel, Prompts.InitialStepPrompt), query);

        // 1. Build the panel agents (for the discussion phase)
        //    These stay kernel-based because the panel conversation is plain text.
        var panelAgents = new List<PanelAgentDescriptor>
        {
            new("Grok",    grokKernel),
            new("ChatGPT", chatGPTKernel),
            new("Gemini",  geminiKernel)
        };

        var judge = new SingleJudgeCrossAnalyzer(Algos.CreateGrokJudgeAgent(grokKernel));
        var orchestrator = new SingleJudgeDialogueMerger(panelAgents, judge);
        orchestrator.TurnOccurred += async (agentName, round, content) =>
        {
            Log.Information("Agent: {agent} | Round: {round}\n{content}\n", agentName, round, content);
            await Task.CompletedTask;
        };

        var initial = new Dictionary<string, StructuredResponse>
        {
            ["Grok"] = grokInitial,
            ["ChatGPT"] = chatGptInitial,
            ["Gemini"] = geminiInitial
        };
        // Multi-agent dialogue + single judge merge
        var (transcript, mergedAfterDialogue) = await orchestrator.RunAsync(query, initial, rounds: 2);

        Log.Information("Final Merged Response after Dialogue:\n{response}", mergedAfterDialogue);
    }

    private static void AddGoogleSearchPlugin(Kernel geminiKernel, Kernel grokKernel, Kernel chatGPTKernel)
    {
        var google = new WebSearchEnginePlugin(googleConnector);
        grokKernel.Plugins.AddFromObject(google, "google");
        chatGPTKernel.Plugins.AddFromObject(google, "google");
        geminiKernel.Plugins.AddFromObject(google, "google");
    }

    public static async Task SingleStructuredTest(ChatCompletionAgent agent, string name, string query)
    {
        var history = new ChatHistory();
        history.AddUserMessage(query);

        try
        {
            await foreach (var response in agent.InvokeAsync(history))
            {
                var msg = response.Message;

                if (msg is ChatMessageContent chat)
                {
                    // Try the convenience Content property first
                    var text = chat.Content;

                    // If Content is null/empty, fall back to TextContent items
                    if (string.IsNullOrWhiteSpace(text) && chat.Items is { Count: > 0 })
                    {
                        var sb = new StringBuilder();
                        foreach (var item in chat.Items)
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

                    // Final fallback: serialize the whole ChatMessageContent if we still have nothing
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        text = JsonSerializer.Serialize(chat, new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                    }

                    Log.Information("Response from {name} Agent:\n\n{content}", name, text);
                }
                else
                {
                    // Non-chat or unexpected type
                    Log.Information("Response from {name} Agent (non-chat message):\n\n{@message}", name, msg);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while invoking {name} Agent", name);
        }
    }


}