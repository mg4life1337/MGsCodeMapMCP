namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Types;

/// <summary>
/// Registers a validated compatibility alias from a path-scoped solution ID to its
/// pre-2.8.0-mgs.7 relative-path-only storage scope.
/// </summary>
public interface ILegacyBaselineAliasRegistry
{
    void RegisterLegacyBaselineAlias(RepoId storageRepoId, RepoId legacyStorageRepoId);
    bool TryGetLegacyBaselineAlias(RepoId storageRepoId, out RepoId legacyStorageRepoId);
}
