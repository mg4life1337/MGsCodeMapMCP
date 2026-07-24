namespace CodeMap.Roslyn;

/// <summary>
/// A strict incremental update could not produce a complete semantic delta.
/// The rolling coordinator must rebuild only the affected solution.
/// </summary>
public sealed class IncrementalCompilationException : InvalidOperationException
{
    public IncrementalCompilationException(string message)
        : base(message) { }
}
