namespace CodeMap.Mcp.Context;

using System.Collections.Concurrent;
using CodeMap.Core.Errors;
using CodeMap.Core.Types;

/// <summary>
/// Thread-safe default <see cref="IRepoRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Normalizes paths on insert to
/// deduplicate <c>"./Foo"</c>, <c>"Foo"</c>, and <c>"Foo/"</c>.
/// </summary>
public sealed class RepoRegistry : IRepoRegistry
{
    private readonly ConcurrentDictionary<string, byte> _repos =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SolutionRegistration>> _solutions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Register(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return;
        _repos.TryAdd(Normalize(repoPath), 0);
    }

    /// <inheritdoc/>
    public void RegisterSolution(string repoPath, SolutionRegistration solution)
    {
        Register(repoPath);
        var solutions = _solutions.GetOrAdd(
            Normalize(repoPath),
            _ => new ConcurrentDictionary<string, SolutionRegistration>(StringComparer.OrdinalIgnoreCase));
        solutions[solution.SolutionId.Value] = solution;
    }

    /// <inheritdoc/>
    public void Forget(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return;
        _repos.TryRemove(Normalize(repoPath), out _);
        _solutions.TryRemove(Normalize(repoPath), out _);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> KnownRepos => _repos.Keys.ToList();

    /// <inheritdoc/>
    public ResolveRepoResult Resolve(string? explicitRepoPath)
    {
        // Explicit path wins, verbatim — downstream (Git) handles its own path canonicalization.
        if (!string.IsNullOrWhiteSpace(explicitRepoPath))
            return new ResolveRepoResult(explicitRepoPath, null);

        var known = _repos.Keys.ToList();
        return known.Count switch
        {
            1 => new ResolveRepoResult(known[0], null),
            0 => new ResolveRepoResult(null, CodeMapError.InvalidArgument(
                "repo_path is required — no repo has been indexed yet. Run index.ensure_baseline first.")),
            _ => new ResolveRepoResult(null, CodeMapError.InvalidArgument(
                $"repo_path is required — {known.Count} repos are indexed: {string.Join(", ", known)}. " +
                "Pass one explicitly.")),
        };
    }

    /// <inheritdoc/>
    public ResolveSolutionResult ResolveSolution(
        string repoPath,
        string? explicitSolutionId,
        string? explicitSolutionPath)
    {
        var repoKey = Normalize(repoPath);
        var known = _solutions.TryGetValue(repoKey, out var values)
            ? values.Values.OrderBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        if (!string.IsNullOrWhiteSpace(explicitSolutionId))
        {
            var match = known.FirstOrDefault(s =>
                string.Equals(s.SolutionId.Value, explicitSolutionId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return new ResolveSolutionResult(match.SolutionId, null, match);
            return Failure($"Unknown solution_id '{explicitSolutionId}'.", known);
        }

        if (!string.IsNullOrWhiteSpace(explicitSolutionPath))
        {
            try
            {
                var id = SolutionId.FromPath(repoPath, explicitSolutionPath);
                var match = known.FirstOrDefault(s => s.SolutionId == id);
                var absolute = Path.GetFullPath(Path.IsPathRooted(explicitSolutionPath)
                    ? explicitSolutionPath
                    : Path.Combine(repoPath, explicitSolutionPath));
                var registration = match ?? new SolutionRegistration(
                    id,
                    SolutionId.GetRepositoryRelativePath(repoPath, absolute),
                    absolute);
                return new ResolveSolutionResult(id, null, registration);
            }
            catch (ArgumentException ex)
            {
                return new ResolveSolutionResult(null, CodeMapError.InvalidArgument(ex.Message));
            }
        }

        return known.Count switch
        {
            0 => new ResolveSolutionResult(null, null), // legacy single-solution baseline compatibility
            1 => new ResolveSolutionResult(known[0].SolutionId, null, known[0]),
            _ => Failure(
                $"Repository has {known.Count} indexed solutions. Pass solution_path or solution_id.",
                known),
        };
    }

    private static ResolveSolutionResult Failure(
        string message,
        IReadOnlyList<SolutionRegistration> known) =>
        new(null, new CodeMapError(
            "AMBIGUOUS_SOLUTION",
            message,
            new Dictionary<string, object>
            {
                ["available_solutions"] = known.Select(s => new
                {
                    solution_id = s.SolutionId.Value,
                    solution_path = s.RelativePath,
                }).ToList(),
            }));

    /// <summary>
    /// Normalizes for registry storage only: absolute path, forward slashes, no trailing slash.
    /// Explicit <c>repo_path</c> arguments are returned by <see cref="Resolve"/> verbatim; this
    /// normalization is used only to dedupe registry keys so <c>Foo</c> and <c>./Foo/</c>
    /// resolve to the same entry.
    /// </summary>
    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
}
