namespace CodeMap.Daemon;

using System.Text.Json;
using CodeMap.Core.Models;

/// <summary>
/// Resolves runtime configuration without consulting the current working directory.
/// </summary>
public sealed record RuntimeConfiguration(
    CodeMapConfig Config,
    string BaseDirectory,
    string ConfigPath,
    string DataDirectory,
    string LogDirectory,
    string? MsBuildPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Resolves configuration using CLI, environment, config-file, then executable-relative defaults.
    /// CLI and environment relative paths are executable-relative; config values are config-file-relative.
    /// </summary>
    public static RuntimeConfiguration Resolve(
        IReadOnlyList<string> args,
        string? baseDirectory = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var exeDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var configArg = GetArgument(args, "--config");
        var configPath = ResolvePath(configArg ?? "codemap.json", exeDir);
        var config = LoadConfig(configPath);
        var configDir = Path.GetDirectoryName(configPath) ?? exeDir;

        var dataValue = FirstNonEmpty(
            GetArgument(args, "--data-dir"),
            getEnvironmentVariable("CODEMAP_DATA_DIR"));
        var dataDirectory = dataValue is not null
            ? ResolvePath(dataValue, exeDir)
            : ResolvePath(config.DataDirectory ?? "data", configDir);

        var logValue = FirstNonEmpty(
            GetArgument(args, "--log-dir"),
            getEnvironmentVariable("CODEMAP_LOG_DIR"));
        var logDirectory = logValue is not null
            ? ResolvePath(logValue, exeDir)
            : ResolvePath(config.LogDirectory ?? "logs", configDir);

        var msBuildOverride = FirstNonEmpty(
            GetArgument(args, "--msbuild-path"),
            getEnvironmentVariable("CODEMAP_MSBUILD_PATH"));
        var msBuildPath = msBuildOverride is not null
            ? ResolvePath(msBuildOverride, exeDir)
            : config.MsBuildPath is null
                ? null
                : ResolvePath(config.MsBuildPath, configDir);

        EnsureWritableDirectory(dataDirectory, "data");
        EnsureWritableDirectory(logDirectory, "log");

        return new RuntimeConfiguration(
            config, exeDir, configPath, dataDirectory, logDirectory, msBuildPath);
    }

    /// <summary>Loads <c>codemap.json</c>; a missing file is valid, malformed JSON is not.</summary>
    public static CodeMapConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return new CodeMapConfig();

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<CodeMapConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException("The configuration file contains JSON null.");
            ValidateIndexingResources(config.IndexingResources);
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not read CodeMap configuration '{configPath}': {ex.Message}", ex);
        }
    }

    private static void ValidateIndexingResources(IndexingResourceConfig? resources)
    {
        if (resources is null) return;
        ValidateRange(resources.MaxConcurrentIndexes, 1, 16, "indexingResources.maxConcurrentIndexes");
        ValidateRange(resources.MaxParallelProjects, 1, 64, "indexingResources.maxParallelProjects");
        ValidateRange(resources.IncrementalSolutionCacheSize, 1, 16, "indexingResources.incrementalSolutionCacheSize");
        ValidateRange(resources.IncrementalSolutionCacheIdleMinutes, 1, 1440, "indexingResources.incrementalSolutionCacheIdleMinutes");
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new InvalidOperationException(
                $"{name} must be between {minimum} and {maximum}; configured value was {value}.");
    }

    internal static string ResolvePath(string value, string relativeTo)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(relativeTo, expanded));
    }

    private static void EnsureWritableDirectory(string path, string purpose)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".codemap-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The configured {purpose} directory is not writable: '{path}'. " +
                $"Use --{purpose}-dir or CODEMAP_{purpose.ToUpperInvariant()}_DIR to select a writable directory. {ex.Message}", ex);
        }
    }

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{name} requires a value.");
                return args[i + 1];
            }

            var prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return args[i][prefix.Length..];
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
