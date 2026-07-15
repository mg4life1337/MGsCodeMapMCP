namespace CodeMap.Roslyn;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Build.Locator;

/// <summary>
/// Ensures MSBuildLocator is registered exactly once per process.
/// Must be called before any Microsoft.CodeAnalysis.MSBuild type is loaded.
/// </summary>
public static class MsBuildInitializer
{
    private static readonly object _lock = new();
    private static bool _registered;

    /// <summary>The instance selected by CodeMap for the current process.</summary>
    public static MsBuildInstanceInfo? SelectedInstance { get; private set; }

    /// <summary>
    /// Registers MSBuild defaults if not already registered. Thread-safe via double-checked locking.
    /// Idempotent — safe to call from multiple call sites.
    /// </summary>
    public static void EnsureRegistered(
        string? requestedPath = null,
        Action<string>? diagnostic = null)
    {
        if (_registered) return;

        lock (_lock)
        {
            if (_registered) return;

            if (!MSBuildLocator.IsRegistered)
            {
                var instances = DiscoverInstances();
                var selected = SelectInstance(instances, requestedPath);
                if (selected is null)
                {
                    var requested = string.IsNullOrWhiteSpace(requestedPath)
                        ? string.Empty
                        : $" Requested path: '{requestedPath}'.";
                    throw new InvalidOperationException(
                        "No compatible MSBuild instance was found. Install Visual Studio 2022/2026, " +
                        "the Visual Studio Build Tools, or a .NET SDK, or configure --msbuild-path." + requested);
                }

                MSBuildLocator.RegisterMSBuildPath(selected.MSBuildPath);
                SelectedInstance = selected;
                diagnostic?.Invoke(
                    $"MGsCodeMapMCP selected MSBuild {selected.Version} ({selected.Kind}) at '{selected.MSBuildPath}'. " +
                    $"Detected instances: {string.Join("; ", instances.Select(FormatInstance))}");
            }
            else
            {
                diagnostic?.Invoke("MSBuild was already registered by the host process.");
            }

            _registered = true;
        }
    }

    /// <summary>
    /// Detects .NET SDK instances through MSBuildLocator and Visual Studio instances through vswhere.
    /// The latter is required because the .NET build of MSBuildLocator only enumerates SDKs.
    /// </summary>
    public static IReadOnlyList<MsBuildInstanceInfo> DiscoverInstances()
    {
        var result = new List<MsBuildInstanceInfo>();

        try
        {
            foreach (var instance in MSBuildLocator.QueryVisualStudioInstances())
            {
                if (Directory.Exists(instance.MSBuildPath))
                {
                    result.Add(new MsBuildInstanceInfo(
                        instance.Name,
                        instance.Version,
                        Path.GetFullPath(instance.MSBuildPath),
                        instance.DiscoveryType.ToString()));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // vswhere may still provide a usable Visual Studio instance.
        }

        foreach (var instance in DiscoverVisualStudioInstances())
            result.Add(instance);

        return result
            .GroupBy(i => i.MSBuildPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.Version).First())
            .OrderByDescending(i => i.Version)
            .ThenBy(i => i.MSBuildPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Selects an explicit instance or the newest installed Visual Studio/MSBuild.</summary>
    public static MsBuildInstanceInfo? SelectInstance(
        IReadOnlyList<MsBuildInstanceInfo> instances,
        string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var normalized = NormalizeRequestedPath(requestedPath);
            var match = instances.FirstOrDefault(i =>
                string.Equals(i.MSBuildPath, normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            if (Directory.Exists(normalized) && IsMsBuildDirectory(normalized))
            {
                var assembly = Path.Combine(normalized, "Microsoft.Build.dll");
                var fileVersion = FileVersionInfo.GetVersionInfo(assembly).FileVersion;
                var version = Version.TryParse(fileVersion?.Split('+')[0], out var parsed)
                    ? parsed
                    : new Version(0, 0);
                return new MsBuildInstanceInfo("Explicit MSBuild", version, normalized, "Explicit");
            }

            return null;
        }

        return instances
            .OrderByDescending(i => i.Kind.StartsWith("VisualStudio", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => i.Version)
            .FirstOrDefault();
    }

    private static IEnumerable<MsBuildInstanceInfo> DiscoverVisualStudioInstances()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        var vswhere = FindVsWhere();
        if (vswhere is null) yield break;

        string json;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-all -products * -requires Microsoft.Component.MSBuild -format json -utf8",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) yield break;
            json = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                yield break;
            }
            if (process.ExitCode != 0) yield break;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("installationPath", out var pathNode) ||
                !item.TryGetProperty("installationVersion", out var versionNode))
                continue;

            var installPath = pathNode.GetString();
            var versionText = versionNode.GetString();
            if (string.IsNullOrWhiteSpace(installPath) || !Version.TryParse(versionText, out var version))
                continue;

            var msBuildPath = Path.Combine(installPath, "MSBuild", "Current", "Bin");
            if (!IsMsBuildDirectory(msBuildPath)) continue;

            var name = item.TryGetProperty("displayName", out var nameNode)
                ? nameNode.GetString()
                : null;
            yield return new MsBuildInstanceInfo(
                name ?? "Visual Studio",
                version,
                Path.GetFullPath(msBuildPath),
                $"VisualStudio{version.Major}");
        }
    }

    private static string? FindVsWhere()
    {
        var fromPath = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim(), "vswhere.exe"))
            .FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var installed = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        return File.Exists(installed) ? installed : null;
    }

    private static string NormalizeRequestedPath(string requestedPath)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath));
        if (File.Exists(path) && Path.GetFileName(path).Equals("MSBuild.exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(path)!;
        if (IsMsBuildDirectory(path)) return path;

        var visualStudioBin = Path.Combine(path, "MSBuild", "Current", "Bin");
        return IsMsBuildDirectory(visualStudioBin) ? visualStudioBin : path;
    }

    private static bool IsMsBuildDirectory(string path) =>
        File.Exists(Path.Combine(path, "Microsoft.Build.dll")) &&
        (File.Exists(Path.Combine(path, "MSBuild.exe")) || File.Exists(Path.Combine(path, "MSBuild.dll")));

    private static string FormatInstance(MsBuildInstanceInfo instance) =>
        $"{instance.Version} {instance.Kind} '{instance.MSBuildPath}'";
}

/// <summary>One installed MSBuild instance available to CodeMap.</summary>
public sealed record MsBuildInstanceInfo(
    string Name,
    Version Version,
    string MSBuildPath,
    string Kind);
