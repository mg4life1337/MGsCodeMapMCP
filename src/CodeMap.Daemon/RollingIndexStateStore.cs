namespace CodeMap.Daemon;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;

/// <summary>Persists rolling state and impact maps below the configured data directory.</summary>
internal sealed class RollingIndexStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(CodeMapJsonOptions.Default)
    {
        WriteIndented = true,
    };
    private readonly string _root;

    public RollingIndexStateStore(string dataDirectory) =>
        _root = Path.Combine(Path.GetFullPath(dataDirectory), "rolling");

    public RollingSolutionStatus? Load(RepoId repoId, SolutionId solutionId, string branch)
    {
        var path = StatePath(repoId, solutionId, branch);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<RollingSolutionStatus>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public IReadOnlyList<RollingSolutionStatus> LoadAll(RepoId repoId)
    {
        return LoadEntries(repoId).Select(entry => entry.State).ToList();
    }

    public RollingSolutionStatus? FindAtCommit(RepoId repoId, SolutionId solutionId, CommitSha commit) =>
        LoadAll(repoId)
            .Where(state => state.SolutionId == solutionId && state.IndexedCommit == commit &&
                            state.IndexState == RollingIndexState.UpToDate)
            .OrderByDescending(state => state.LastUpdatedAt)
            .FirstOrDefault();

    public void Save(RollingSolutionStatus state)
    {
        var path = StatePath(state.RepoId, state.SolutionId, state.Branch);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var previous = Load(state.RepoId, state.SolutionId, state.Branch);
        if (previous is not null && previous.WorkspaceId != state.WorkspaceId)
        {
            var historyDirectory = Path.Combine(Path.GetDirectoryName(path)!, "history");
            Directory.CreateDirectory(historyDirectory);
            var historyPath = Path.Combine(historyDirectory,
                $"{previous.LastUpdatedAt.UtcTicks}-{StableId(previous.WorkspaceId.Value)}.json");
            if (!File.Exists(historyPath))
                AtomicWrite(historyPath, JsonSerializer.Serialize(previous, JsonOptions));
        }
        AtomicWrite(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    public SolutionImpactMap? LoadImpactMap(RepoId repoId, SolutionId solutionId)
    {
        var path = ImpactPath(repoId, solutionId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SolutionImpactMap>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public void SaveImpactMap(RepoId repoId, SolutionId solutionId, SolutionImpactMap map)
    {
        var path = ImpactPath(repoId, solutionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, JsonSerializer.Serialize(map, JsonOptions));
    }

    public IReadOnlyList<RollingSolutionStatus> ApplyRetention(RepoId repoId, int retentionDays, int maxBranches)
    {
        var entries = LoadEntries(repoId)
            .OrderByDescending(entry => entry.State.LastUpdatedAt)
            .ToList();
        var keepBranches = entries.Where(entry => entry.IsCurrent).Select(entry => entry.State.Branch)
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(1, maxBranches))
            .ToHashSet(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays));
        var removedEntries = entries.Where(entry =>
                entry.State.LastUpdatedAt < cutoff &&
                (!entry.IsCurrent || !keepBranches.Contains(entry.State.Branch)))
            .ToList();
        foreach (var entry in removedEntries)
        {
            try
            {
                if (entry.IsCurrent)
                    Directory.Delete(Path.GetDirectoryName(entry.Path)!, recursive: true);
                else
                    File.Delete(entry.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removedEntries.Select(entry => entry.State).ToList();
    }

    private string RepoDirectory(RepoId repoId) => Path.Combine(_root, repoId.Value);
    private string StatePath(RepoId repoId, SolutionId solutionId, string branch) =>
        Path.Combine(RepoDirectory(repoId), "solutions", solutionId.Value, "branches", StableId(branch), "state.json");
    private string ImpactPath(RepoId repoId, SolutionId solutionId) =>
        Path.Combine(RepoDirectory(repoId), "impact", solutionId.Value + ".json");

    private IReadOnlyList<StateEntry> LoadEntries(RepoId repoId)
    {
        var repoDirectory = RepoDirectory(repoId);
        if (!Directory.Exists(repoDirectory)) return [];
        var paths = Directory.EnumerateFiles(repoDirectory, "state.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, IsCurrent: true))
            .Concat(Directory.EnumerateFiles(repoDirectory, "*.json", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "history", StringComparison.OrdinalIgnoreCase))
                .Select(path => (Path: path, IsCurrent: false)));
        var entries = new List<StateEntry>();
        foreach (var item in paths)
        {
            try
            {
                var state = JsonSerializer.Deserialize<RollingSolutionStatus>(File.ReadAllText(item.Path), JsonOptions);
                if (state is not null) entries.Add(new StateEntry(state, item.Path, item.IsCurrent));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return entries;
    }

    internal static string StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record StateEntry(RollingSolutionStatus State, string Path, bool IsCurrent);
}
