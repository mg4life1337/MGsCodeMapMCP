namespace CodeMap.Mcp.Context;

using CodeMap.Core.Errors;

internal sealed class RollingGenerationUnavailableException : InvalidOperationException
{
    public CodeMapError Error { get; }

    public RollingGenerationUnavailableException(CodeMapError error)
        : base(error.Message)
    {
        Error = error;
    }
}
