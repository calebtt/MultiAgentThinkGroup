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