namespace CodeMap.Daemon;

using System.Reflection;
using CodeMap.Core.Interfaces;
using CodeMap.Daemon.Logging;
using CodeMap.Mcp;
using CodeMap.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// CodeMap daemon entry point.
/// Startup sequence: --version check → config.json load → MSBuild init →
/// logging setup (stderr + file) → DI container build → MCP tool registration →
/// shutdown hook (token savings) → MCP stdio loop.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the CodeMap MCP server.
    /// Handles <c>--version</c> / <c>-v</c> flags, loads <c>~/.codemap/config.json</c>,
    /// then runs the MCP JSON-RPC server over stdin/stdout until EOF or Ctrl-C.
    /// </summary>
    internal static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--version" or "-v")
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "1.0.0";
            Console.WriteLine($"MGsCodeMapMCP {version} (upstream CodeMap 2.8.0)");
            return;
        }

        RuntimeConfiguration runtime;
        try
        {
            runtime = RuntimeConfiguration.Resolve(args);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"MGsCodeMapMCP startup failed: {ex.Message}");
            Environment.ExitCode = 2;
            return;
        }

        var config = runtime.Config;

        // Resolve log level: config.json, then default
        var logLevel = Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        // MSBuild registration MUST happen before any Roslyn workspace use.
        try
        {
            MsBuildInitializer.EnsureRegistered(runtime.MsBuildPath, Console.Error.WriteLine);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"MGsCodeMapMCP MSBuild initialization failed: {ex.Message}");
            Environment.ExitCode = 3;
            return;
        }

        var builder = Host.CreateDefaultBuilder(args);

        // Logging: stderr (real-time) + structured JSON file (persistent)
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(logLevel);
            logging.AddConsole(options =>
                options.LogToStandardErrorThreshold = LogLevel.Trace);
            logging.AddProvider(new FileLoggerProvider(runtime.LogDirectory, logLevel));
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(runtime);
            services.AddCodeMapServices(
                baseDir: runtime.DataDirectory,
                sharedCacheDir: Environment.GetEnvironmentVariable("CODEMAP_CACHE_DIR")
                    ?? config.SharedCacheDir);
            services.AddHostedService<RepositorySupervisor>();
        });

        var host = builder.Build();

        // Register all MCP tools after DI container is ready
        ServiceRegistration.RegisterMcpTools(host.Services);

        // Save token savings on process exit (graceful shutdown only)
        if (host.Services.GetRequiredService<ITokenSavingsTracker>() is CodeMap.Query.TokenSavingsTracker tracker)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => tracker.SaveToDisk();

        // Run MCP server over stdin/stdout until the client disconnects
        var mcpServer = host.Services.GetRequiredService<McpServer>();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await host.StartAsync(cts.Token).ConfigureAwait(false);
        try
        {
            await mcpServer.RunAsync(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                cts.Token).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

}
