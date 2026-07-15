namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>End-to-end proof that one Git commit can own isolated VB.NET solution baselines.</summary>
[Trait("Category", "Integration")]
public sealed class MultiSolutionVbIsolationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codemap-multisln-vb-{Guid.NewGuid():N}");
    private readonly CommitSha _sha = CommitSha.From(new string('a', 40));
    private readonly RepoId _repoId = RepoId.From("multi-solution-vb-repo");
    private QueryEngine _engine = null!;
    private RoutingContext _solutionA = null!;
    private RoutingContext _solutionB = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var solutionAPath = CreateSolutionA();
        var solutionBPath = CreateSolutionB();
        Repository.Init(_root);
        using (var repository = new Repository(_root))
        {
            Commands.Stage(repository, "*");
            var signature = new Signature("CodeMap Tests", "tests@codemap.local", DateTimeOffset.UtcNow);
            repository.Commit("VB multi-solution fixture", signature, signature);
        }

        MsBuildInitializer.EnsureRegistered();
        var store = new CustomSymbolStore(Path.Combine(_root, "portable-data", "repositories"));
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var solutionAId = SolutionId.FromPath(_root, solutionAPath);
        var solutionBId = SolutionId.FromPath(_root, solutionBPath);
        var scopedA = SolutionScope.ToStorageRepoId(_repoId, solutionAId);
        var scopedB = SolutionScope.ToStorageRepoId(_repoId, solutionBId);

        var compiledA = await compiler.CompileAndExtractAsync(solutionAPath);
        var compiledB = await compiler.CompileAndExtractAsync(solutionBPath);
        await store.CreateBaselineAsync(scopedA, _sha, compiledA, _root);
        await store.CreateBaselineAsync(scopedB, _sha, compiledB, _root);

        _engine = new QueryEngine(
            store, new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(store), new GraphTraverser(),
            new FeatureTracer(store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
        _solutionA = new RoutingContext(repoId: scopedA, baselineCommitSha: _sha);
        _solutionB = new RoutingContext(repoId: scopedB, baselineCommitSha: _sha);
    }

    [Fact]
    public void SameCommit_CreatesTwoPhysicallySeparateSolutionBaselines()
    {
        var solutionsRoot = Path.Combine(_root, "portable-data", "repositories",
            Sanitize(_repoId.Value), "solutions");

        Directory.GetDirectories(solutionsRoot).Should().HaveCount(2);
        Directory.GetFiles(solutionsRoot, "manifest.json", SearchOption.AllDirectories).Should().HaveCount(2);
        Directory.Exists(Path.Combine(_root, ".codemap")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, ".codex")).Should().BeFalse();
    }

    [Fact]
    public async Task VbSymbols_SourceSpan_Calls_AndInterfaceAreIndexed()
    {
        var worker = await FindAsync(_solutionA, "Worker", SymbolKind.Class);
        var run = await FindAsync(_solutionA, "Run", SymbolKind.Method, ".Worker.Run");
        var helper = await FindAsync(_solutionA, "Helper", SymbolKind.Method, ".Worker.Helper");

        worker.FilePath.Value.Should().EndWith("SolutionA/Worker.vb");
        run.Line.Should().BeGreaterThan(1);
        var span = await _engine.GetDefinitionSpanAsync(_solutionA, run.SymbolId, 30, 0);
        span.IsSuccess.Should().BeTrue();
        span.Value.Data.Content.Should().Contain("Return Helper() + 1");
        span.Value.Data.FilePath.Value.Should().EndWith("SolutionA/Worker.vb");

        var callees = await _engine.GetCalleesAsync(_solutionA, run.SymbolId, 2, 20, null);
        callees.IsSuccess.Should().BeTrue();
        callees.Value.Data.Nodes.Should().Contain(node => node.SymbolId == helper.SymbolId);
        var callers = await _engine.GetCallersAsync(_solutionA, helper.SymbolId, 2, 20, null);
        callers.IsSuccess.Should().BeTrue();
        callers.Value.Data.Nodes.Should().Contain(node => node.SymbolId == run.SymbolId);

        var hierarchy = await _engine.GetTypeHierarchyAsync(_solutionA, worker.SymbolId);
        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.Interfaces.Should().Contain(item => item.DisplayName.Contains("IWorker"));
    }

    [Fact]
    public async Task QueriesNeverLeakSymbolsAcrossSolutions()
    {
        var inA = await _engine.SearchSymbolsAsync(_solutionA, "ForeignOnly", null, new BudgetLimits(maxResults: 10));
        var inB = await _engine.SearchSymbolsAsync(_solutionB, "ForeignOnly", null, new BudgetLimits(maxResults: 10));
        var workerInB = await _engine.SearchSymbolsAsync(_solutionB, "Worker", null, new BudgetLimits(maxResults: 10));

        inA.Value.Data.Hits.Should().BeEmpty();
        inB.Value.Data.Hits.Should().ContainSingle(hit => hit.Kind == SymbolKind.Class);
        workerInB.Value.Data.Hits.Should().BeEmpty();
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return ValueTask.CompletedTask;
    }

    private async Task<SymbolSearchHit> FindAsync(
        RoutingContext routing,
        string name,
        SymbolKind kind,
        string? fullyQualifiedNameFragment = null)
    {
        var result = await _engine.SearchSymbolsAsync(
            routing, name, new SymbolSearchFilters(Kinds: [kind]), new BudgetLimits(maxResults: 10));
        result.IsSuccess.Should().BeTrue();
        return result.Value.Data.Hits.First(hit => fullyQualifiedNameFragment is null
            ? hit.FullyQualifiedName.Contains(name)
            : hit.FullyQualifiedName.Contains(fullyQualifiedNameFragment));
    }

    private string CreateSolutionA()
    {
        var directory = Path.Combine(_root, "SolutionA");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SolutionA.vbproj"), Project("SolutionA"));
        File.WriteAllText(Path.Combine(directory, "Worker.vb"), """
            Namespace Alpha
                Public Interface IWorker
                    Function Run() As Integer
                End Interface

                Public Class Worker
                    Implements IWorker

                    Public Function Run() As Integer Implements IWorker.Run
                        Return Helper() + 1
                    End Function

                    Private Function Helper() As Integer
                        Return 41
                    End Function
                End Class
            End Namespace
            """);
        var solution = Path.Combine(_root, "Alpha.slnx");
        File.WriteAllText(solution, "<Solution><Project Path=\"SolutionA/SolutionA.vbproj\" /></Solution>");
        return solution;
    }

    private string CreateSolutionB()
    {
        var directory = Path.Combine(_root, "SolutionB");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SolutionB.vbproj"), Project("SolutionB"));
        File.WriteAllText(Path.Combine(directory, "ForeignOnly.vb"), """
            Namespace Beta
                Public Class ForeignOnly
                    Public Sub Execute()
                    End Sub
                End Class
            End Namespace
            """);
        var solution = Path.Combine(_root, "Beta.slnx");
        File.WriteAllText(solution, "<Solution><Project Path=\"SolutionB/SolutionB.vbproj\" /></Solution>");
        return solution;
    }

    private static string Project(string rootNamespace) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <RootNamespace>{{rootNamespace}}</RootNamespace>
          </PropertyGroup>
        </Project>
        """;

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
}
