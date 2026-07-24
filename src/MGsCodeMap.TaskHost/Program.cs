namespace MGsCodeMap.TaskHost;

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// Windowless Windows Task Scheduler action that owns the daemon child lifetime.
/// It intentionally has no reference to Roslyn, storage, MCP, or other CodeMap assemblies.
/// </summary>
internal static class Program
{
    private const int StartupFailureExitCode = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => arg is "--version" or "-v"))
        {
            Console.WriteLine($"MGsCodeMapMCP Task Host {Version}");
            return 0;
        }

        TaskHostSettings? settings = null;
        try
        {
            settings = TaskHostSettings.Resolve(args);
            TaskHostLog.Write(settings, "starting");

            var startInfo = new ProcessStartInfo(settings.DaemonPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = settings.InstallDirectory,
            };
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(settings.ConfigPath);
            foreach (var argument in settings.ForwardedArguments)
                startInfo.ArgumentList.Add(argument);

            using var daemon = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The daemon process could not be started.");
            TaskHostLog.Write(settings, $"daemon_started daemon_pid={daemon.Id}");
            daemon.WaitForExit();
            TaskHostLog.Write(settings, $"daemon_exited exit_code={daemon.ExitCode}");
            return daemon.ExitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
                                   InvalidOperationException or UnauthorizedAccessException)
        {
            TaskHostLog.Write(settings, $"startup_failed error={Sanitize(ex.Message)}");
            return StartupFailureExitCode;
        }
    }

    private static string Version => typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "2.8.0-mgs.7";

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}

internal sealed record TaskHostSettings(
    string InstallDirectory,
    string DaemonPath,
    string ConfigPath,
    IReadOnlyList<string> ForwardedArguments)
{
    internal static TaskHostSettings Resolve(IReadOnlyList<string> args)
    {
        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        string? daemon = null;
        string? config = null;
        var forwarded = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            if (TryReadOption(args, ref index, "--daemon", out var daemonValue))
                daemon = daemonValue;
            else if (TryReadOption(args, ref index, "--config", out var configValue))
                config = configValue;
            else
                forwarded.Add(args[index]);
        }

        var daemonPath = ResolvePath(daemon ?? "MGsCodeMap.Daemon.exe", installDirectory);
        var configPath = ResolvePath(config ?? "codemap.json", installDirectory);
        if (!File.Exists(daemonPath))
            throw new InvalidOperationException($"Daemon executable not found: {daemonPath}");
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"Configuration file not found: {configPath}");

        return new TaskHostSettings(installDirectory, daemonPath, configPath, forwarded);
    }

    private static bool TryReadOption(
        IReadOnlyList<string> args,
        ref int index,
        string name,
        out string? value)
    {
        var argument = args[index];
        if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
        {
            if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            value = args[index];
            return true;
        }

        var prefix = name + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument[prefix.Length..];
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{name} requires a value.");
            return true;
        }

        value = null;
        return false;
    }

    private static string ResolvePath(string value, string relativeTo) =>
        Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(relativeTo, value));
}

internal static class TaskHostLog
{
    internal static void Write(TaskHostSettings? settings, string message)
    {
        try
        {
            var logDirectory = ResolveLogDirectory(settings);
            Directory.CreateDirectory(logDirectory);
            var line = $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDirectory, "taskhost.log"), line);
        }
        catch
        {
            // A diagnostic log failure must never change the supervised daemon exit code.
        }
    }

    private static string ResolveLogDirectory(TaskHostSettings? settings)
    {
        if (settings is null)
            return Path.Combine(AppContext.BaseDirectory, "logs");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settings.ConfigPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.TryGetProperty("logDirectory", out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
            {
                var value = Environment.ExpandEnvironmentVariables(property.GetString()!);
                var configDirectory = Path.GetDirectoryName(settings.ConfigPath) ?? settings.InstallDirectory;
                return Path.GetFullPath(Path.IsPathRooted(value)
                    ? value
                    : Path.Combine(configDirectory, value));
            }
        }
        catch (JsonException)
        {
            // The daemon will report malformed configuration when it starts.
        }

        return Path.Combine(settings.InstallDirectory, "logs");
    }
}
