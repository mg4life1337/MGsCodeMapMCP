namespace CodeMap.Core.Models;

/// <summary>
/// Settings loaded from <c>codemap.json</c> at startup.
/// Relative paths are resolved against the directory containing that file.
/// Changes require a daemon restart (hot-reload is not supported).
/// </summary>
public record CodeMapConfig(
    string? LogLevel = "Information",
    string? SharedCacheDir = null,
    BudgetOverrides? BudgetOverrides = null,
    string? DataDirectory = null,
    string? LogDirectory = null,
    string? MsBuildPath = null,
    IReadOnlyList<RepositoryRootConfig>? RepositoryRoots = null,
    IReadOnlyList<RepositoryConfig>? Repositories = null,
    IndexingResourceConfig? IndexingResources = null
);

/// <summary>
/// Process-wide resource limits for semantic indexing. Missing configuration uses these
/// conservative defaults so repository discovery cannot start several memory-intensive
/// Roslyn builds at once.
/// </summary>
public sealed record IndexingResourceConfig(
    int MaxConcurrentIndexes = 1,
    int MaxParallelProjects = 2,
    int IncrementalSolutionCacheSize = 1,
    int IncrementalSolutionCacheIdleMinutes = 5,
    bool MemoryTelemetry = true
);

/// <summary>Automatic repository and solution discovery beneath a common root.</summary>
public record RepositoryRootConfig(
    string Path,
    bool DiscoverGitRepositories = true,
    bool DiscoverSolutions = true,
    bool AutoIndex = false,
    bool WatchGitHead = false,
    int WatchIntervalSeconds = 3,
    IReadOnlyList<string>? Exclude = null,
    string? DefaultSolution = null,
    string IndexMode = "commit",
    string UpdateStrategy = "full",
    bool CheckAllSolutions = true,
    bool SkipUnaffectedSolutions = true,
    bool ServePreviousIndexWhileUpdating = true,
    int RetentionDays = 30,
    int MaxRollingBranches = 8,
    int FullRebuildChangeThreshold = 5000
);

/// <summary>Explicit repository configuration. Explicit solutions take precedence over discovery.</summary>
public record RepositoryConfig(
    string Root,
    IReadOnlyList<string>? Solutions = null,
    bool AutoIndex = false,
    bool WatchGitHead = false,
    int WatchIntervalSeconds = 3,
    bool DiscoverSolutions = true,
    string? DefaultSolution = null,
    string IndexMode = "commit",
    string UpdateStrategy = "full",
    bool CheckAllSolutions = true,
    bool SkipUnaffectedSolutions = true,
    bool ServePreviousIndexWhileUpdating = true,
    int RetentionDays = 30,
    int MaxRollingBranches = 8,
    int FullRebuildChangeThreshold = 5000
);

/// <summary>Budget limit overrides for hardcap enforcement.</summary>
public record BudgetOverrides(
    int? MaxResults = null,
    int? MaxLines = null,
    int? MaxChars = null
);
