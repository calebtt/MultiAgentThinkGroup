using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using MultiAgentThinkGroup;
using Serilog;
using Serilog.Events;
using System.Text;
using System.Text.Json;

/// <summary>
/// A place to put free functions.
/// </summary>
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

    //public static Kernel BuildKernel(string apiKey, string modelName, string endPoint)
    //{
    //    var builder = Kernel.CreateBuilder();
    //    builder.AddOpenAIChatCompletion(
    //        modelId: modelName,
    //        apiKey: apiKey,
    //        endpoint: new Uri(endPoint));
    //    return builder.Build();
    //}

    public const int MaxFileChars = 8000; // tune for token limits
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
}