using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.Grok;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using MultiAgentThinkGroup;
using Serilog;
using Serilog.Events;
using System.Text;

/// <summary>
/// A place to put free functions.
/// </summary>
public static partial class Algos
{
    public const int MaxTokens = 7000;
    public const int MaxFileChars = 8000; // tune for token limits

    public static ChatCompletionAgent CreateGrokAgent(Kernel kernel, string instructions) => new()
    {
        Kernel = kernel,
        Instructions = instructions,
        Arguments = new(new GrokPromptExecutionSettings
        {
            ToolCallBehavior = GrokToolCallBehavior.AutoInvokeKernelFunctions,
            StructuredOutputMode = GrokStructuredOutputMode.JsonSchema,
            StructuredOutputSchema = StructuredResponse.GetXaiSchema(),
            // Reasoning effort level only supported on old models from xAI,
            // model choice denotes reasoning level.
            MaxTokens = MaxTokens
        })
    };

    public static ChatCompletionAgent CreateGrokJudgeAgent(Kernel kernel) => new()
    {
        Kernel = kernel,
        Instructions = Prompts.CrossAnalysisJudgePrompt,
        Arguments = new(new GrokPromptExecutionSettings
        {
            ToolCallBehavior = GrokToolCallBehavior.AutoInvokeKernelFunctions,
            StructuredOutputMode = GrokStructuredOutputMode.JsonSchema,
            StructuredOutputSchema = StructuredResponse.GetXaiSchema(),
            MaxTokens = MaxTokens
        })
    };

    public static ChatCompletionAgent CreateGeminiAgent(Kernel kernel, string instructions) => new()
    {
        Kernel = kernel,
        Instructions = instructions,
        Arguments = new(new GeminiPromptExecutionSettings
        {
            ToolCallBehavior = GeminiToolCallBehavior.AutoInvokeKernelFunctions,
            ResponseMimeType = "application/json",
            // Let SK generate the JSON Schema for Gemini
            ResponseSchema = typeof(StructuredResponse),
            MaxTokens = MaxTokens,
            ThinkingConfig = new() { ThinkingLevel = "low" }
        })
    };

    public static ChatCompletionAgent CreateChatGPTAgent(Kernel kernel, string instructions) => new()
    {
        Kernel = kernel,
        Instructions = instructions,
        Arguments = new(new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            // Let SK generate JSON Schema for StructuredResponse
            ResponseFormat = typeof(StructuredResponse),
            MaxTokens = MaxTokens,
            ReasoningEffort = "low"
        })
    };

    public static void AddConsoleLogger(string? logName)
    {
        var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Debug,
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

    public static ChatHistory BuildHistoryWithFiles(
        string question,
        IEnumerable<string> filePaths)
    {
        var history = new ChatHistory();

        // Your main question
        history.AddUserMessage(question);

        foreach (var path in filePaths)
        {
            if (!File.Exists(path))
            {
                history.AddUserMessage($"(Warning: file not found: {path})");
                continue;
            }

            var fileName = Path.GetFileName(path);
            var content = File.ReadAllText(path);

            var trimmed = content.Length > MaxFileChars
                ? content.Substring(0, MaxFileChars) + "\n// [truncated]"
                : content;

            // Make it clear and structured for the model
            var sb = new StringBuilder();
            sb.AppendLine($"Here is the C# source file `{fileName}`:");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(trimmed);
            sb.AppendLine("```");

            history.AddUserMessage(sb.ToString());
        }

        return history;
    }

    public static string ExtractText(IReadOnlyList<ChatMessageContent> messages)
    {
        var reply = messages.LastOrDefault();
        if (reply is null) return string.Empty;

        if (!string.IsNullOrWhiteSpace(reply.Content))
        {
            return reply.Content;
        }

        var parts = reply.Items
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return string.Join(" ", parts);
    }
}