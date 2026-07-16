namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Git;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Exercises a real VB.NET project-reference delta through baseline, overlay, and merged queries.</summary>
[Trait("Category", "Integration")]
public sealed class RollingVbDeltaTests : IAsyncLifetime
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), $"codemap-vb-rolling-{Guid.NewGuid():N}");
    private string _repoRoot = null!;
    private string _solutionPath = null!;
    private RepoId _repoId;
    private CommitSha _baselineCommit;
    private WorkspaceId _workspaceId;
    private WorkspaceManager _manager = null!;
    private MergedQueryEngine _engine = null!;
    private IOverlayStore _overlay = null!;
    private ISymbolStore _store = null!;
    private CapturingIncrementalCompiler _incremental = null!;

    public async ValueTask InitializeAsync()
    {
        _repoRoot = Path.Combine(_testRoot, "repository");
        var dataRoot = Path.Combine(_testRoot, "data");
        Directory.CreateDirectory(_repoRoot);
        CreateFixture();
        Repository.Init(_repoRoot);
        _baselineCommit = Commit("initial VB fixture");

        MsBuildInitializer.EnsureRegistered();
        var store = new CustomSymbolStore(Path.Combine(dataRoot, "repositories"));
        _store = store;
        var overlay = new CustomEngineOverlayStore(store, Path.Combine(dataRoot, "repositories"));
        _overlay = overlay;
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var compiled = await compiler.CompileAndExtractAsync(_solutionPath);
        _repoId = RepoId.From("rolling-vb-integration");
        await store.CreateBaselineAsync(_repoId, _baselineCommit, compiled, _repoRoot);

        _incremental = new CapturingIncrementalCompiler(new IncrementalCompiler(
            new SymbolDiffer(NullLogger<SymbolDiffer>.Instance),
            NullLogger<IncrementalCompiler>.Instance));
        var git = new GitService(NullLogger<GitService>.Instance);
        var cache = new InMemoryCacheService();
        _manager = new WorkspaceManager(
            overlay, _incremental, store, git, cache,
            new ResolutionWorker(NullLogger<ResolutionWorker>.Instance),
            NullLogger<WorkspaceManager>.Instance);
        _workspaceId = WorkspaceId.From("rolling-vb-test");
        var created = await _manager.CreateWorkspaceAsync(
            _repoId, _workspaceId, _baselineCommit, _solutionPath, _repoRoot);
        created.IsSuccess.Should().BeTrue();

        var excerpt = new ExcerptReader(store);
        var graph = new GraphTraverser();
        var inner = new QueryEngine(
            store, cache, new TokenSavingsTracker(), excerpt, graph,
            new FeatureTracer(store, graph), NullLogger<QueryEngine>.Instance);
        _engine = new MergedQueryEngine(
            inner, overlay, _manager, cache, new TokenSavingsTracker(),
            excerpt, graph, NullLogger<MergedQueryEngine>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_testRoot, recursive: true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Delta_UpdatesRenamesDependenciesPartialsDesignerFilesAndRelations()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "Library", "Worker.vb"), """
            Namespace Library
                Public Partial Class Worker
                    Inherits WorkerBase
                    Implements IWorker

                    Public Function NewName(value As String) As Integer Implements IWorker.Run
                        Return value.Length
                    End Function
                End Class
            End Namespace
            """);
        File.WriteAllText(Path.Combine(_repoRoot, "Application", "Caller.vb"), """
            Imports Library
            Namespace Application
                Public Class Caller
                    Public Function Execute() As Integer
                        Dim worker = New Worker()
                        Return worker.NewName("updated")
                    End Function
                End Class
            End Namespace
            """);
        File.WriteAllText(Path.Combine(_repoRoot, "Library", "Added.vb"), """
            Namespace Library
                Public Class AddedType
                    Public Function AddedMethod() As Integer
                        Return 7
                    End Function
                End Class
            End Namespace
            """);
        File.Delete(Path.Combine(_repoRoot, "Library", "Removed.vb"));
        _ = Commit("incremental VB change");

        var baselineRemoved = await _store.GetSymbolsByFileAsync(
            _repoId, _baselineCommit, FilePath.From("Library/Removed.vb"));
        baselineRemoved.Should().Contain(symbol =>
            symbol.SymbolId.Value.Contains("RemovedType", StringComparison.Ordinal));
        var changes = await new GitService(NullLogger<GitService>.Instance)
            .GetChangedFilesAsync(_repoRoot, _baselineCommit);
        changes.Should().Contain(change =>
            change.FilePath.Value == "Library/Removed.vb" && change.Kind == FileChangeKind.Deleted ||
            change.Kind == FileChangeKind.Renamed && change.OldFilePath.HasValue &&
            change.OldFilePath.Value.Value == "Library/Removed.vb");

        var refreshed = await _manager.RefreshOverlayAsync(_repoId, _workspaceId, null);

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.FilesReindexed.Should().BeGreaterThan(2);
        var authoritativeFiles = await _overlay.GetOverlayFilePathsAsync(_repoId, _workspaceId);
        authoritativeFiles.Should().Contain(FilePath.From("Library/Removed.vb"));
        var routing = new RoutingContext(
            _repoId, _workspaceId, ConsistencyMode.Workspace, _baselineCommit);

        (await Search(routing, "OldName")).Should().BeEmpty();
        (await Search(routing, "RemovedType")).Should().BeEmpty();
        var newMethod = (await Search(routing, "NewName")).Should().ContainSingle().Subject;
        (await Search(routing, "AddedMethod")).Should().ContainSingle();
        (await Search(routing, "WorkerDesignerPart")).Should().ContainSingle();

        newMethod.FullyQualifiedName.Should().Contain("Worker.NewName");
        newMethod.FilePath.Value.Should().EndWith("Library/Worker.vb");
        newMethod.Line.Should().BeGreaterThan(1);

        var overlayCallers = await _overlay.GetOverlayReferencesAsync(
            _repoId, _workspaceId, newMethod.SymbolId, null, 20);
        _incremental.LastDelta!.AddedOrUpdatedReferences.Should().Contain(reference =>
            reference.FromSymbol.Value.Contains("Execute", StringComparison.Ordinal) &&
            reference.ToSymbol == newMethod.SymbolId);
        overlayCallers.Should().Contain(reference =>
            reference.FromSymbol.Value.Contains("Execute", StringComparison.Ordinal));
        var callers = await _engine.GetCallersAsync(routing, newMethod.SymbolId, 2, 20, null);
        callers.IsSuccess.Should().BeTrue();
        callers.Value.Data.Nodes.Should().Contain(node => node.DisplayName.Contains("Execute"));

        var worker = (await Search(routing, "Worker", SymbolKind.Class))
            .First(hit => hit.FullyQualifiedName.EndsWith(".Worker", StringComparison.Ordinal));
        _incremental.LastDelta!.TypeRelations.Should().Contain(relation =>
            relation.TypeSymbolId == worker.SymbolId && relation.RelationKind == TypeRelationKind.BaseType);
        var overlayRelations = await _overlay.GetOverlayTypeRelationsAsync(
            _repoId, _workspaceId, worker.SymbolId);
        overlayRelations.Should().Contain(relation => relation.RelationKind == TypeRelationKind.BaseType);
        var hierarchy = await _engine.GetTypeHierarchyAsync(routing, worker.SymbolId);
        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.BaseType.Should().NotBeNull();
        hierarchy.Value.Data.BaseType!.DisplayName.Should().Contain("WorkerBase");
        hierarchy.Value.Data.Interfaces.Should().Contain(item => item.DisplayName.Contains("IWorker"));

        Directory.Exists(Path.Combine(_repoRoot, ".codemap")).Should().BeFalse();
        Directory.Exists(Path.Combine(_repoRoot, ".codex")).Should().BeFalse();
    }

    [Fact]
    public async Task TriviaOnlyVbChange_ProducesNoSemanticDeltaButServesCurrentSource()
    {
        const string comment = "' current source note";
        File.AppendAllText(Path.Combine(_repoRoot, "Library", "Worker.vb"), Environment.NewLine + comment);
        _ = Commit("trivia-only VB change");

        var refreshed = await _manager.RefreshOverlayAsync(_repoId, _workspaceId, null);

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.FilesReindexed.Should().Be(0);
        refreshed.Value.SymbolsUpdated.Should().Be(0);
        refreshed.Value.Metrics!.Mode.Should().Be(IncrementalUpdateMode.NoOp);
        refreshed.Value.Metrics.SemanticNoOpFiles.Should().Be(1);

        var routing = new RoutingContext(
            _repoId, _workspaceId, ConsistencyMode.Workspace, _baselineCommit);
        (await Search(routing, "OldName")).Should().ContainSingle();
        var span = await _engine.GetSpanAsync(
            routing, FilePath.From("Library/Worker.vb"), 1, 100, 0, null);
        span.IsSuccess.Should().BeTrue();
        span.Value.Data.Content.Should().Contain(comment);
    }

    [Fact]
    public async Task VbMethodBodyChange_ReindexesOnlyChangedDocument()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "Application", "Caller.vb"), """
            Imports Library
            Namespace Application
                Public Class Caller
                    Public Function Execute() As Integer
                        Dim worker = New Worker()
                        Return worker.OldName("body update")
                    End Function
                End Class
            End Namespace
            """);
        _ = Commit("VB body change");

        var refreshed = await _manager.RefreshOverlayAsync(_repoId, _workspaceId, null);

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.FilesReindexed.Should().Be(1);
        refreshed.Value.Metrics!.Mode.Should().Be(IncrementalUpdateMode.Document);
        refreshed.Value.Metrics.DocumentsReindexed.Should().Be(1);
        refreshed.Value.Metrics.AffectedProjects.Should().BeGreaterThanOrEqualTo(1);
        _incremental.LastDelta!.AddedOrUpdatedReferences.Should().Contain(reference =>
            reference.FromSymbol.Value.Contains("Execute", StringComparison.Ordinal) &&
            reference.ToSymbol.Value.Contains("OldName", StringComparison.Ordinal));

        var routing = new RoutingContext(
            _repoId, _workspaceId, ConsistencyMode.Workspace, _baselineCommit);
        (await Search(routing, "Execute")).Should().ContainSingle();
    }

    private async Task<IReadOnlyList<SymbolSearchHit>> Search(
        RoutingContext routing,
        string query,
        SymbolKind? kind = null)
    {
        var result = await _engine.SearchSymbolsAsync(
            routing,
            query,
            kind is null ? null : new SymbolSearchFilters(Kinds: [kind.Value]),
            new BudgetLimits(maxResults: 20));
        result.IsSuccess.Should().BeTrue();
        return result.Value.Data.Hits;
    }

    private void CreateFixture()
    {
        File.WriteAllText(Path.Combine(_repoRoot, ".gitignore"), "bin/\nobj/\n");
        Directory.CreateDirectory(Path.Combine(_repoRoot, "Library"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "Application"));
        File.WriteAllText(Path.Combine(_repoRoot, "Library", "Library.vbproj"), Project(""));
        File.WriteAllText(Path.Combine(_repoRoot, "Application", "Application.vbproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace></RootNamespace></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Library\Library.vbproj" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_repoRoot, "Library", "Contracts.vb"), """
            Namespace Library
                Public Interface IWorker
                    Function Run(value As String) As Integer
                End Interface
                Public MustInherit Class WorkerBase
                End Class
            End Namespace
            """);
        File.WriteAllText(Path.Combine(_repoRoot, "Library", "Worker.vb"), """
            Namespace Library
                Public Partial Class Worker
                    Inherits WorkerBase
                    Implements IWorker
                    Public Function OldName(value As String) As Integer Implements IWorker.Run
                        Return value.Length
                    End Function
                End Class
            End Namespace
            """);
        File.WriteAllText(Path.Combine(_repoRoot, "Library", "Worker.Designer.vb"), """
            Namespace Library
                Partial Public Class Worker
                    Public Property WorkerDesignerPart As Integer
                End Class
            End Namespace
            """);
        File.WriteAllText(Path.Combine(_repoRoot, "Library", "Removed.vb"), """
            Namespace Library
                Public Class RemovedType
                End Class
            End Namespace
            """);
        File.WriteAllText(Path.Combine(_repoRoot, "Application", "Caller.vb"), """
            Imports Library
            Namespace Application
                Public Class Caller
                    Public Function Execute() As Integer
                        Return New Worker().OldName("initial")
                    End Function
                End Class
            End Namespace
            """);
        _solutionPath = Path.Combine(_repoRoot, "Primary.slnx");
        File.WriteAllText(_solutionPath,
            "<Solution><Project Path=\"Library/Library.vbproj\" /><Project Path=\"Application/Application.vbproj\" /></Solution>");
    }

    private CommitSha Commit(string message)
    {
        using var repository = new Repository(_repoRoot);
        foreach (var entry in repository.RetrieveStatus())
        {
            if (entry.State.HasFlag(FileStatus.DeletedFromWorkdir))
                Commands.Remove(repository, entry.FilePath);
            else
                Commands.Stage(repository, entry.FilePath);
        }
        var signature = new Signature("CodeMap Tests", "tests@codemap.local", DateTimeOffset.UtcNow);
        return CommitSha.From(repository.Commit(message, signature, signature).Sha);
    }

    private static string Project(string rootNamespace) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>{{rootNamespace}}</RootNamespace></PropertyGroup>
        </Project>
        """;

    private sealed class CapturingIncrementalCompiler(IIncrementalCompiler inner) : IIncrementalCompiler
    {
        public OverlayDelta? LastDelta { get; private set; }

        public async Task<OverlayDelta> ComputeDeltaAsync(
            string solutionPath, string repoRootPath, IReadOnlyList<FilePath> changedFiles,
            ISymbolStore baselineStore, RepoId repoId, CommitSha commitSha,
            int currentRevision, CancellationToken ct = default)
        {
            LastDelta = await inner.ComputeDeltaAsync(
                solutionPath, repoRootPath, changedFiles, baselineStore, repoId,
                commitSha, currentRevision, ct);
            return LastDelta;
        }

        public void Dispose() => inner.Dispose();
    }
}
