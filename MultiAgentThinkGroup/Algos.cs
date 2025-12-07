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

}