namespace CodeMap.Core.Models;

/// <summary>
/// Settings loaded from the portable <c>codemap.json</c> file at startup.
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
    IReadOnlyList<RepositoryConfig>? Repositories = null
);

/// <summary>Automatic repository and solution discovery beneath a common root.</summary>
public record RepositoryRootConfig(
    string Path,
    bool DiscoverGitRepositories = true,
    bool DiscoverSolutions = true,
    bool AutoIndex = false,
    bool WatchGitHead = false,
    int WatchIntervalSeconds = 3,
    IReadOnlyList<string>? Exclude = null
);

/// <summary>Explicit repository configuration. Explicit solutions take precedence over discovery.</summary>
public record RepositoryConfig(
    string Root,
    IReadOnlyList<string>? Solutions = null,
    bool AutoIndex = false,
    bool WatchGitHead = false,
    int WatchIntervalSeconds = 3
);

/// <summary>Budget limit overrides for hardcap enforcement.</summary>
public record BudgetOverrides(
    int? MaxResults = null,
    int? MaxLines = null,
    int? MaxChars = null
);
