using Microsoft.SemanticKernel;
using MultiAgentThinkGroup;
using Serilog;
using Serilog.Events;
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

    public static Kernel BuildKernel(string apiKey, string modelName, string endPoint)
    {
        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(
            modelId: modelName,
            apiKey: apiKey,
            endpoint: new Uri(endPoint));
        return builder.Build();
    }

    public static async Task<LanguageModelConfig> LoadLanguageModelConfigAsync(string profilePath)
    {
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException($"Profile file not found: {profilePath}");
        }

        var json = await File.ReadAllTextAsync(profilePath);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("LanguageModel", out var lmElement))
        {
            throw new InvalidOperationException("LanguageModel section is missing in the profile JSON.");
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var languageModel = JsonSerializer.Deserialize<LanguageModelConfig>(lmElement.GetRawText(), options)
            ?? throw new InvalidOperationException("Failed to deserialize LanguageModel configuration.");

        // Validate PromptSections: Skip empties, dedupe by Type (keep last)
        var sections = languageModel.PromptSections ?? new List<PromptSection>();
        var invalids = sections.Where(s => string.IsNullOrWhiteSpace(s.Content)).ToList();
        if (invalids.Any())
        {
            Log.Warning("Skipping {InvalidCount} empty PromptSections.", invalids.Count);
            sections = sections.Except(invalids).ToList();
        }
        sections = sections.GroupBy(s => s.Type).Select(g => g.Last()).ToList();
        languageModel = languageModel with { PromptSections = sections };

        // Validate Rules: Skip invalids, dedupe by Id (keep first)
        var rules = languageModel.Rules ?? new List<Rule>();
        var ruleInvalids = rules.Where(r => string.IsNullOrWhiteSpace(r.Description)).ToList();
        if (ruleInvalids.Any())
        {
            Log.Warning("Skipping {InvalidCount} invalid Rules.", ruleInvalids.Count);
            rules = rules.Except(ruleInvalids).ToList();
        }
        rules = rules.GroupBy(r => r.Id).Select(g => g.First()).ToList();
        languageModel = languageModel with { Rules = rules };

        // Validate ToolInstructions: Skip empties
        if (languageModel.ToolInstructions?.Any() == true)
        {
            var invalid = languageModel.ToolInstructions.Where(ti => string.IsNullOrWhiteSpace(ti.Guidance)).ToList();
            if (invalid.Any())
            {
                Log.Warning("Skipping {InvalidCount} invalid ToolInstructions.", invalid.Count);
                languageModel = languageModel with { ToolInstructions = languageModel.ToolInstructions.Except(invalid).ToList() };
            }
        }

        return languageModel;
    }

}