namespace CodeMap.Daemon;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Daemon.Logging;
using CodeMap.Mcp;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Central, multi-session Streamable HTTP host.</summary>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => arg is "--version" or "-v"))
        {
            Console.WriteLine($"MGsCodeMapMCP {Version}");
            return 0;
        }

        RuntimeConfiguration runtime;
        try { runtime = RuntimeConfiguration.Resolve(args); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"MGsCodeMap daemon startup failed: {ex.Message}");
            return 2;
        }

        DaemonInstanceLock? instanceLock = null;
        if (runtime.Server.SingleInstance &&
            !DaemonInstanceLock.TryAcquire(runtime, Version, out instanceLock, out var existing))
        {
            await ReportExistingInstanceAsync(existing, runtime).ConfigureAwait(false);
            return DaemonInstanceLock.AlreadyRunningExitCode;
        }

        using (instanceLock)
        {
            try { MsBuildInitializer.EnsureRegistered(runtime.MsBuildPath, Console.Error.WriteLine); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Console.Error.WriteLine($"MGsCodeMap daemon MSBuild initialization failed: {ex.Message}");
                return 3;
            }

            var logLevel = Enum.TryParse<LogLevel>(runtime.Config.LogLevel, true, out var parsed)
                ? parsed : LogLevel.Information;
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls($"http://{runtime.Server.Host}:{runtime.Server.Port}");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(logLevel);
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddProvider(new FileLoggerProvider(runtime.LogDirectory, logLevel));

            builder.Services.Configure<HostOptions>(options =>
                options.ShutdownTimeout = TimeSpan.FromSeconds(runtime.Server.ShutdownTimeoutSeconds));
            builder.Services.AddSingleton(runtime);
            builder.Services.AddSingleton<DaemonRuntimeState>();
            builder.Services.AddSingleton<McpSessionRegistry>();
            builder.Services.AddCodeMapServices(
                runtime.DataDirectory,
                Environment.GetEnvironmentVariable("CODEMAP_CACHE_DIR") ?? runtime.Config.SharedCacheDir);
            builder.Services.AddSingleton<RepositorySupervisor>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RepositorySupervisor>());

            var app = builder.Build();
            ServiceRegistration.RegisterMcpTools(app.Services);
            MapEndpoints(app, runtime);

            if (app.Services.GetRequiredService<ITokenSavingsTracker>() is TokenSavingsTracker tracker)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => tracker.SaveToDisk();

            var mode = args.Contains("--console", StringComparer.OrdinalIgnoreCase) ? "console"
                : args.Contains("--service", StringComparer.OrdinalIgnoreCase) ? "service" : "background";
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
            logger.LogInformation(
                "MGsCodeMapMCP {Version} PID={Pid} mode={Mode} endpoint={Endpoint} data={DataDirectory} logs={LogDirectory} msbuild={MsBuild} single_instance={SingleInstance} repositories={RepositoryCount}",
                Version, Environment.ProcessId, mode, runtime.McpEndpoint, runtime.DataDirectory,
                runtime.LogDirectory, runtime.MsBuildPath ?? "auto", runtime.Server.SingleInstance,
                (runtime.Config.Repositories?.Count ?? 0) + (runtime.Config.RepositoryRoots?.Count ?? 0));

            try
            {
                await app.RunAsync().ConfigureAwait(false);
                return 0;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine(
                    $"MGsCodeMap daemon could not listen on {runtime.McpEndpoint}: {ex.Message}");
                return 4;
            }
        }
    }

    private static string Version => typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "2.8.0-mgs.8";

    private static void MapEndpoints(WebApplication app, RuntimeConfiguration runtime)
    {
        var mcpPath = RuntimeConfiguration.NormalizeHttpPath(runtime.Server.McpPath);
        var healthPath = RuntimeConfiguration.NormalizeHttpPath(runtime.Server.HealthPath);

        app.MapPost(mcpPath, async (HttpContext context, McpServer server,
            McpSessionRegistry sessions, DaemonRuntimeState state,
            RuntimeActivityTracker activity, ILoggerFactory loggerFactory) =>
        {
            if (state.IsStopping) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (context.Request.ContentLength is > McpServer.MaxContentLength)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
                body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(body) > McpServer.MaxContentLength)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var requestedSession = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
            var sessionId = sessions.Touch(requestedSession);
            context.Response.Headers["Mcp-Session-Id"] = sessionId;
            state.RequestObserved();
            using var requestActivity = activity.BeginRequest();
            var (requestId, method, tool) = DescribeRequest(body);
            loggerFactory.CreateLogger("McpHttp").LogInformation(
                "MCP request session={SessionId} request={RequestId} method={Method} tool={Tool}",
                sessionId, requestId, method, tool ?? "-");
            var response = await server.ProcessMessageAsync(body, sessionId, context.RequestAborted)
                .ConfigureAwait(false);
            if (response is null) return Results.Accepted();

            var json = response.ToJsonString();
            var accepts = context.Request.GetTypedHeaders().Accept;
            var sseOnly = accepts is { Count: > 0 } &&
                accepts.Any(value => value.MediaType.Value == "text/event-stream") &&
                !accepts.Any(value => value.MediaType.Value is "application/json" or "*/*");
            return sseOnly
                ? Results.Text($"event: message\ndata: {json}\n\n", "text/event-stream", Encoding.UTF8)
                : Results.Text(json, "application/json", Encoding.UTF8);
        });

        app.MapDelete(mcpPath, (HttpContext context, McpSessionRegistry sessions) =>
        {
            var id = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
            return sessions.Remove(id) ? Results.NoContent() : Results.NotFound();
        });

        app.MapGet(mcpPath, () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

        app.MapGet(healthPath, (McpSessionRegistry sessions, DaemonRuntimeState state,
             RepositorySupervisor supervisor, RollingIndexCoordinator rolling,
             IndexingResourceGate indexing, WorkspaceManager workspaces,
             ISymbolStore symbolStore, IncrementalCompiler incremental,
             RuntimeActivityTracker activity) =>
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var gcMemory = GC.GetGCMemoryInfo();
            var custom = symbolStore as CustomSymbolStore;
            return Results.Json(new
            {
                product = "MGsCodeMapMCP",
                version = Version,
                processId = process.Id,
                startTimeUtc = state.StartedAtUtc,
                mode = state.IsStopping ? "stopping" : "running",
                dataDirectory = runtime.DataDirectory,
                endpoint = runtime.McpEndpoint,
                activeSessions = sessions.Count,
                loadedSolutions = rolling.TrackedSolutionCount,
                trackedSolutions = rolling.TrackedSolutionCount,
                logicalWorkspaces = workspaces.OpenWorkspaceCount,
                roslynIncrementalCacheLoaded = incremental.CachedSolutions,
                openWorkspaces = workspaces.OpenWorkspaceCount,
                openBaselines = custom?.OpenBaselineCount ?? 0,
                openOverlays = custom?.OpenOverlayCount ?? 0,
                openBaselineReaders = custom?.OpenBaselineCount ?? 0,
                openOverlayReaders = custom?.OpenOverlayCount ?? 0,
                solutionCache = new
                {
                    loaded = incremental.CachedSolutions,
                    hits = incremental.CacheHits,
                    misses = incremental.CacheMisses,
                    evictions = incremental.CacheEvictions,
                },
                memory = new
                {
                    workingSetBytes = process.WorkingSet64,
                    privateBytes = process.PrivateMemorySize64,
                    managedHeapBytes = gcMemory.HeapSizeBytes,
                    fragmentedBytes = gcMemory.FragmentedBytes,
                },
                repositorySupervisor = new
                {
                    status = supervisor.IsRunning ? "running" : "idle",
                    observedSolutions = supervisor.ObservedSolutionCount,
                },
                indexing = new
                {
                    activeFullIndexes = indexing.ActiveIndexes,
                    activeRollingQueues = rolling.ActiveQueueCount,
                    publishing = indexing.ActiveIndexes > 0 ||
                        rolling.ActiveQueueCount > 0 ||
                        activity.ActivePublications > 0,
                    activeIncrementalUpdates = activity.ActiveIncrementalUpdates,
                },
                requestCount = state.RequestCount,
            });
        });

        app.MapPost("/shutdown", (DaemonRuntimeState state, IHostApplicationLifetime lifetime) =>
        {
            if (!state.BeginStopping()) return Results.Accepted();
            _ = Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                lifetime.StopApplication();
            });
            return Results.Accepted();
        });
    }

    private static async Task ReportExistingInstanceAsync(
        DaemonLockInfo? existing,
        RuntimeConfiguration runtime)
    {
        var healthEndpoint = existing?.HealthEndpoint ?? runtime.HealthEndpoint;
        var reachable = false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(healthEndpoint).ConfigureAwait(false);
            reachable = response.IsSuccessStatusCode;
        }
        catch { }

        Console.Error.WriteLine("MGsCodeMap daemon is already running.");
        if (existing is not null)
        {
            Console.Error.WriteLine($"PID: {existing.ProcessId}");
            Console.Error.WriteLine($"Endpoint: {existing.McpEndpoint}");
            Console.Error.WriteLine($"Data directory: {existing.DataDirectory}");
        }
        Console.Error.WriteLine($"Health: {(reachable ? "reachable" : "not yet reachable")}");
    }

    private static (string RequestId, string Method, string? Tool) DescribeRequest(string body)
    {
        try
        {
            var request = JsonNode.Parse(body) as JsonObject;
            return (
                request?["id"]?.ToJsonString() ?? "notification",
                request?["method"]?.GetValue<string>() ?? "unknown",
                (request?["params"] as JsonObject)?["name"]?.GetValue<string>());
        }
        catch (JsonException) { return ("invalid", "invalid", null); }
    }
}
