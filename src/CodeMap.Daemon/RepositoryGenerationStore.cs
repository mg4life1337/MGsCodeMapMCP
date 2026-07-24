namespace CodeMap.Daemon;

using System.Text;
using System.Text.Json;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;

/// <summary>
/// Durable generation manifests. Only <c>active-generation.json</c> controls
/// query visibility; history files are seed candidates and never publish state.
/// </summary>
internal sealed class RepositoryGenerationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(CodeMapJsonOptions.Default)
    {
        WriteIndented = true,
    };
    private readonly string _root;

    public RepositoryGenerationStore(string dataDirectory) =>
        _root = Path.Combine(Path.GetFullPath(dataDirectory), "rolling");

    public RepositoryIndexGeneration? LoadActive(RepoId repoId, string repositoryPath) =>
        Read(ActivePath(repoId, repositoryPath));

    public IReadOnlyList<RepositoryIndexGeneration> LoadHistory(
        RepoId repoId,
        string repositoryPath) =>
        Directory.Exists(HistoryDirectory(repoId, repositoryPath))
            ? Directory.EnumerateFiles(HistoryDirectory(repoId, repositoryPath), "*.json")
                .Select(Read)
                .Where(generation => generation is not null)
                .Cast<RepositoryIndexGeneration>()
                .OrderByDescending(generation => generation.PublishedAt)
                .ToList()
            : [];

    public StagingRepositoryGeneration? LoadStaging(
        RepoId repoId,
        string repositoryPath) =>
        ReadStaging(StagingPath(repoId, repositoryPath));

    public void BeginStaging(
        string repositoryPath,
        StagingRepositoryGeneration staging) =>
        AtomicWrite(
            StagingPath(staging.RepoId, repositoryPath),
            JsonSerializer.Serialize(staging, JsonOptions));

    public void Activate(string repositoryPath, RepositoryIndexGeneration generation)
    {
        var historyDirectory = HistoryDirectory(generation.RepoId, repositoryPath);
        Directory.CreateDirectory(historyDirectory);
        var json = JsonSerializer.Serialize(generation, JsonOptions);
        // Publish the single active pointer first. History is advisory seed data;
        // a crash after publication may omit history but can never expose a
        // history-only generation as active.
        AtomicWrite(ActivePath(generation.RepoId, repositoryPath), json);
        AtomicWrite(
            Path.Combine(historyDirectory, generation.GenerationId + ".json"),
            json);
    }

    public void CompleteStaging(
        RepoId repoId,
        string repositoryPath,
        string generationId)
    {
        var path = StagingPath(repoId, repositoryPath);
        var current = ReadStaging(path);
        if (current is null ||
            !string.Equals(
                current.GenerationId,
                generationId,
                StringComparison.Ordinal))
            return;
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
    }

    public void CleanupIncomplete(RepoId repoId, string repositoryPath)
    {
        var directory = RepositoryDirectory(repoId, repositoryPath);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private RepositoryIndexGeneration? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<RepositoryIndexGeneration>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private StagingRepositoryGeneration? ReadStaging(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<StagingRepositoryGeneration>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string RepositoryDirectory(RepoId repoId, string repositoryPath) =>
        Path.Combine(
            _root,
            repoId.Value,
            "instances",
            RollingIndexStateStore.StableId(NormalizeRepositoryPath(repositoryPath)),
            "generations");

    private string ActivePath(RepoId repoId, string repositoryPath) =>
        Path.Combine(
            RepositoryDirectory(repoId, repositoryPath),
            "active-generation.json");

    private string StagingPath(RepoId repoId, string repositoryPath) =>
        Path.Combine(
            RepositoryDirectory(repoId, repositoryPath),
            "staging-generation.json");

    private string HistoryDirectory(RepoId repoId, string repositoryPath) =>
        Path.Combine(RepositoryDirectory(repoId, repositoryPath), "history");

    private static string NormalizeRepositoryPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(content);
        using (var stream = new FileStream(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   64 * 1024,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(temporary, path, destinationBackupFileName: null);
        else
            File.Move(temporary, path);
    }
}

internal sealed record StagingRepositoryGeneration(
    string GenerationId,
    RepoId RepoId,
    IReadOnlyList<StagingWorkspaceBinding> Workspaces);

internal sealed record StagingWorkspaceBinding(
    SolutionId SolutionId,
    WorkspaceId WorkspaceId);
