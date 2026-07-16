namespace CodeMap.Core.Models;

/// <summary>Scope selected for an incremental semantic update.</summary>
public enum IncrementalUpdateMode
{
    NoOp,
    Document,
    Project,
    Dependency,
    Full,
}

/// <summary>Timings for the observable stages of an incremental update.</summary>
public sealed record IncrementalUpdateTimings(
    TimeSpan GitDiff = default,
    TimeSpan SolutionOpen = default,
    TimeSpan ChangeClassification = default,
    TimeSpan SyntaxSemanticDiff = default,
    TimeSpan ApiFingerprint = default,
    TimeSpan DirectCompilation = default,
    TimeSpan DependencyResolution = default,
    TimeSpan SymbolExtraction = default,
    TimeSpan ReferenceExtraction = default,
    TimeSpan TypeRelations = default,
    TimeSpan BaselineOverlayDiff = default,
    TimeSpan OverlayWrite = default,
    TimeSpan Total = default);

/// <summary>Counts, selected scope, fallback reason, and timings for one update.</summary>
public sealed record IncrementalUpdateMetrics(
    IncrementalUpdateMode Mode,
    string? FallbackReason,
    int ChangedFiles,
    int SemanticNoOpFiles,
    int DocumentsReindexed,
    int AffectedProjects,
    int SymbolsWritten,
    int SymbolsDeleted,
    int RelationsUpdated,
    IncrementalUpdateTimings Timings);
