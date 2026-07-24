namespace CodeMap.Daemon;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeMap.Core.Models;

/// <summary>
/// Persistable, repository-relative map used to decide which solutions are affected by a Git delta.
/// It deliberately avoids storing machine-specific absolute paths.
/// </summary>
public sealed record SolutionImpactMap(
    string SolutionPath,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ProjectImpactNode> Projects)
{
    private static readonly HashSet<string> SourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".vb", ".fs" };
    private static readonly HashSet<string> ProjectExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".vbproj", ".fsproj" };
    private static readonly HashSet<string> GlobalBuildNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
            "NuGet.Config", "global.json", ".editorconfig",
        };

    public static SolutionImpactMap Build(string repoRoot, string solutionPath)
    {
        var root = Path.GetFullPath(repoRoot);
        var solution = Path.GetFullPath(solutionPath);
        var projectPaths = ReadSolutionProjects(solution)
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(Path.GetDirectoryName(solution)!, path)))
            .Where(path => File.Exists(path) && IsInside(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projects = projectPaths.Select(path => BuildProject(root, path)).ToList();
        return new SolutionImpactMap(Relative(root, solution), DateTimeOffset.UtcNow, projects);
    }

    public SolutionImpactResult Analyze(IReadOnlyList<FileChange> changes)
    {
        var changed = changes
            .SelectMany(change => change.OldFilePath is { } old
                ? new[] { Normalize(change.FilePath.Value), Normalize(old.Value) }
                : new[] { Normalize(change.FilePath.Value) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (changed.Contains(Normalize(SolutionPath)))
            return new SolutionImpactResult(true, changes.Count, "solution membership changed", true);

        var directlyAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Projects)
        {
            if (changed.Contains(Normalize(project.ProjectPath)) ||
                project.Files.Any(file => changed.Contains(Normalize(file))) ||
                project.GlobalInputs.Any(file => changed.Contains(Normalize(file))))
            {
                directlyAffected.Add(project.ProjectPath);
                continue;
            }

            var prefix = Normalize(project.ProjectDirectory).TrimEnd('/') + "/";
            if (changed.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                    SourceExtensions.Contains(Path.GetExtension(file))))
                directlyAffected.Add(project.ProjectPath);

            if (changed.Any(file => IsGlobalBuildInput(file) &&
                                    IsProjectBelowInput(project.ProjectDirectory, file)))
                directlyAffected.Add(project.ProjectPath);
        }

        if (directlyAffected.Count == 0)
            return new SolutionImpactResult(false, 0, "0 changed inputs", false);

        var affected = new HashSet<string>(directlyAffected, StringComparer.OrdinalIgnoreCase);
        bool expanded;
        do
        {
            expanded = false;
            foreach (var project in Projects)
            {
                if (affected.Contains(project.ProjectPath)) continue;
                if (project.ProjectReferences.Any(affected.Contains))
                    expanded |= affected.Add(project.ProjectPath);
            }
        } while (expanded);

        var rebuildMap = changed.Any(file =>
            ProjectExtensions.Contains(Path.GetExtension(file)) || IsGlobalBuildInput(file));
        return new SolutionImpactResult(
            true,
            changed.Count,
            $"{affected.Count} affected project(s)",
            rebuildMap);
    }

    /// <summary>
    /// Returns solution-relevant inputs and their similarity weights. Inputs not
    /// reachable from this solution are deliberately absent.
    /// </summary>
    public IReadOnlyDictionary<string, int> GetWeightedInputs()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AddWeightedInput(result, SolutionPath, 8);
        foreach (var project in Projects)
        {
            AddWeightedInput(result, project.ProjectPath, 5);
            foreach (var reference in project.ProjectReferences)
                AddWeightedInput(result, reference, 5);
            foreach (var global in project.GlobalInputs)
                AddWeightedInput(result, global, 8);
            foreach (var file in project.Files)
                AddWeightedInput(result, file, 1, overwrite: false);
        }
        return result;
    }

    public int CountChangedProjects(IReadOnlyList<FileChange> changes)
    {
        var changed = changes
            .SelectMany(change => change.OldFilePath is { } old
                ? new[] { Normalize(change.FilePath.Value), Normalize(old.Value) }
                : new[] { Normalize(change.FilePath.Value) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Projects.Count(project =>
            changed.Contains(Normalize(project.ProjectPath)) ||
            project.Files.Any(file => changed.Contains(Normalize(file))) ||
            project.GlobalInputs.Any(file => changed.Contains(Normalize(file))));
    }

    private static ProjectImpactNode BuildProject(string repoRoot, string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        XDocument? document = null;
        try { document = XDocument.Load(projectPath, LoadOptions.None); }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { }

        if (document is not null)
        {
            foreach (var element in document.Descendants())
            {
                var localName = element.Name.LocalName;
                var include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                if (localName == "ProjectReference")
                {
                    AddIfInside(repoRoot, Path.Combine(projectDirectory, include), references);
                }
                else if (localName is "Compile" or "AdditionalFiles" or "Analyzer" or "EditorConfigFiles")
                {
                    if (!include.Contains('*') && !include.Contains('?'))
                        AddIfInside(repoRoot, Path.Combine(projectDirectory, include), files);
                }
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories))
            {
                var segments = Path.GetRelativePath(projectDirectory, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(segment => segment is "bin" or "obj" or ".git" or ".vs")) continue;
                if (SourceExtensions.Contains(Path.GetExtension(file))) AddIfInside(repoRoot, file, files);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var globalInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var directory = new DirectoryInfo(projectDirectory);
             directory is not null && IsInside(repoRoot, directory.FullName);
             directory = directory.Parent)
        {
            foreach (var name in GlobalBuildNames)
            {
                var candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate)) AddIfInside(repoRoot, candidate, globalInputs);
            }
            if (string.Equals(Path.GetFullPath(directory.FullName).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) break;
        }

        return new ProjectImpactNode(
            Relative(repoRoot, projectPath),
            Relative(repoRoot, projectDirectory),
            files.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            references.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            globalInputs.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IReadOnlyList<string> ReadSolutionProjects(string solutionPath)
    {
        if (Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return XDocument.Load(solutionPath).Descendants()
                    .Select(element => element.Attribute("Path")?.Value ?? element.Attribute("path")?.Value)
                    .Where(path => path is not null && ProjectExtensions.Contains(Path.GetExtension(path)))
                    .Select(path => path!)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { return []; }
        }

        try
        {
            var regex = new Regex("Project\\([^)]*\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+\\.(?:csproj|vbproj|fsproj))\"",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return File.ReadLines(solutionPath)
                .Select(line => regex.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["path"].Value)
                .ToList();
        }
        catch (IOException) { return []; }
    }

    private static void AddIfInside(string root, string path, ISet<string> target)
    {
        var full = Path.GetFullPath(path);
        if (IsInside(root, full)) target.Add(Relative(root, full));
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Relative(string root, string path) => Normalize(Path.GetRelativePath(root, path));
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool IsGlobalBuildInput(string path) =>
        GlobalBuildNames.Contains(Path.GetFileName(path)) ||
        Path.GetExtension(path).ToLowerInvariant() is ".globalconfig" or ".ruleset";

    private static bool IsProjectBelowInput(string projectDirectory, string inputPath)
    {
        var inputDirectory = Normalize(Path.GetDirectoryName(inputPath) ?? "").TrimEnd('/');
        var project = Normalize(projectDirectory).TrimEnd('/');
        return inputDirectory.Length == 0 ||
               project.Equals(inputDirectory, StringComparison.OrdinalIgnoreCase) ||
               project.StartsWith(inputDirectory + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddWeightedInput(
        IDictionary<string, int> target,
        string path,
        int weight,
        bool overwrite = true)
    {
        var normalized = Normalize(path);
        if (Path.IsPathRooted(normalized) ||
            normalized == ".." ||
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment == "..") ||
            normalized.Contains(':', StringComparison.Ordinal))
            return;
        if (overwrite)
            target[normalized] = weight;
        else
            target.TryAdd(normalized, weight);
    }
}

public sealed record ProjectImpactNode(
    string ProjectPath,
    string ProjectDirectory,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> GlobalInputs);

public readonly record struct SolutionImpactResult(
    bool IsAffected,
    int ChangedInputCount,
    string Reason,
    bool RebuildMap);
