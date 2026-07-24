namespace CodeMap.Daemon;

using System.Collections.Concurrent;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>Thread-safe publication boundary for rolling repository generations.</summary>
public sealed class RollingGenerationRegistry : IRollingGenerationRegistry
{
    private readonly RepositoryGenerationStore _store;
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public RollingGenerationRegistry(RuntimeConfiguration runtime)
    {
        _store = new RepositoryGenerationStore(runtime.DataDirectory);
    }

    public void BeginUpdate(
        string repoPath,
        RepositorySnapshot target,
        bool servePreviousIndexWhileUpdating)
    {
        _store.CleanupIncomplete(target.RepoId, repoPath);
        _entries.AddOrUpdate(
            Normalize(repoPath),
            _ => new Entry(
                _store.LoadActive(target.RepoId, repoPath),
                target,
                Updating: true,
                servePreviousIndexWhileUpdating),
            (_, current) => current with
            {
                Active = current.Active ?? _store.LoadActive(target.RepoId, repoPath),
                Target = target,
                Updating = true,
                ServePrevious = servePreviousIndexWhileUpdating,
            });
    }

    public void Activate(string repoPath, RepositoryIndexGeneration generation)
    {
        _store.Activate(repoPath, generation);
        _entries.AddOrUpdate(
            Normalize(repoPath),
            _ => new Entry(generation, null, Updating: false, ServePrevious: false),
            (_, current) => current with
            {
                Active = generation,
                Target = null,
                Updating = false,
                ServePrevious = false,
            });
    }

    public void Fail(string repoPath, RepositorySnapshot target)
    {
        _entries.AddOrUpdate(
            Normalize(repoPath),
            _ => new Entry(
                _store.LoadActive(target.RepoId, repoPath),
                target,
                false,
                false),
            (_, current) => current.Target?.GenerationId == target.GenerationId
                ? current with { Updating = false }
                : current);
    }

    public RepositoryIndexGeneration? GetActive(string repoPath) =>
        _entries.TryGetValue(Normalize(repoPath), out var entry)
            ? entry.Active
            : null;

    public RollingGenerationResolution Resolve(string repoPath, SolutionId solutionId)
    {
        if (!_entries.TryGetValue(Normalize(repoPath), out var entry))
            return new(RollingGenerationAvailability.NotManaged, null, null, false);

        var activeBinding = entry.Active?.Solutions.FirstOrDefault(
            solution => solution.SolutionId == solutionId);
        if (!entry.Updating)
        {
            if (entry.Target is not null)
            {
                var activeMatchesFailedTarget = entry.Active is not null &&
                    string.Equals(entry.Active.Branch, entry.Target.Branch, StringComparison.Ordinal) &&
                    entry.Active.HeadCommit == entry.Target.HeadCommit &&
                    string.Equals(
                        entry.Active.WorkingTreeFingerprint,
                        entry.Target.WorkingTreeFingerprint,
                        StringComparison.Ordinal);
                if (!activeMatchesFailedTarget)
                    return new(
                        RollingGenerationAvailability.NotReady,
                        null,
                        entry.Target.GenerationId,
                        false);
            }
            return activeBinding is null
                ? new(RollingGenerationAvailability.NotReady, null, entry.Active?.GenerationId, false)
                : new(
                    RollingGenerationAvailability.Ready,
                    activeBinding.WorkspaceId,
                    entry.Active!.GenerationId,
                    false);
        }

        var activeMatchesTarget = entry.Active is not null &&
            entry.Target is not null &&
            string.Equals(entry.Active.Branch, entry.Target.Branch, StringComparison.Ordinal) &&
            entry.Active.HeadCommit == entry.Target.HeadCommit &&
            string.Equals(
                entry.Active.WorkingTreeFingerprint,
                entry.Target.WorkingTreeFingerprint,
                StringComparison.Ordinal);

        if (activeBinding is not null && (activeMatchesTarget || entry.ServePrevious))
            return new(
                RollingGenerationAvailability.Ready,
                activeBinding.WorkspaceId,
                entry.Active!.GenerationId,
                ServingPrevious: !activeMatchesTarget);

        return new(RollingGenerationAvailability.Updating, null, entry.Target?.GenerationId, false);
    }

    internal IReadOnlyList<RepositoryIndexGeneration> LoadHistory(
        RepoId repoId,
        string repositoryPath) =>
        _store.LoadHistory(repoId, repositoryPath);

    internal StagingRepositoryGeneration? LoadStaging(
        RepoId repoId,
        string repositoryPath) =>
        _store.LoadStaging(repoId, repositoryPath);

    internal void BeginStaging(
        string repositoryPath,
        StagingRepositoryGeneration staging) =>
        _store.BeginStaging(repositoryPath, staging);

    internal void CompleteStaging(
        RepoId repoId,
        string repositoryPath,
        string generationId) =>
        _store.CompleteStaging(repoId, repositoryPath, generationId);

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private sealed record Entry(
        RepositoryIndexGeneration? Active,
        RepositorySnapshot? Target,
        bool Updating,
        bool ServePrevious);
}
