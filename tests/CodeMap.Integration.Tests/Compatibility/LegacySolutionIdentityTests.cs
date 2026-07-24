namespace CodeMap.Integration.Tests.Compatibility;

using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Handlers;
using CodeMap.Storage.Engine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

[Trait("Category", "Integration")]
public sealed class LegacySolutionIdentityTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"codemap-legacy-identity-{Guid.NewGuid():N}");
    private CustomSymbolStore _store = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new CustomSymbolStore(Path.Combine(_tempDir, "store"));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Mgs6Baseline_IsReusedOnlyByItsRecordedRepositoryInstance()
    {
        var rootA = Path.Combine(_tempDir, "clone-a");
        var rootB = Path.Combine(_tempDir, "clone-b");
        var solutionA = CreateSolution(rootA);
        var solutionB = CreateSolution(rootB);
        var publicRepoId = RepoId.From("same-remote-repository");
        var sha = CommitSha.From("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var currentA = SolutionId.FromPath(rootA, solutionA);
        var currentB = SolutionId.FromPath(rootB, solutionB);
        var legacy = SolutionId.LegacyFromPath(rootA, solutionA);
        var legacyStorage = SolutionScope.ToStorageRepoId(publicRepoId, legacy);

        currentA.Should().NotBe(currentB);
        SolutionId.LegacyFromPath(rootB, solutionB).Should().Be(legacy);

        await _store.CreateBaselineAsync(
            legacyStorage,
            sha,
            EmptyCompilation(solutionA),
            rootA);

        var git = Substitute.For<IGitService>();
        git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(publicRepoId);
        git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(sha);

        var compiler = Substitute.For<IRoslynCompiler>();
        compiler.CompileAndExtractAsync(solutionB, Arg.Any<CancellationToken>())
            .Returns(EmptyCompilation(solutionB));
        var cache = Substitute.For<IBaselineCacheManager>();
        cache.PullAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        cache.PushAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var handler = new IndexHandler(
            git,
            compiler,
            _store,
            cache,
            new RepoRegistry(),
            NullLogger<IndexHandler>.Instance);

        var reused = await handler.HandleAsync(Args(rootA, solutionA), CancellationToken.None);
        reused.IsError.Should().BeFalse();
        reused.Content.Should().Contain("\"already_existed\":true");
        await compiler.DidNotReceive()
            .CompileAndExtractAsync(solutionA, Arg.Any<CancellationToken>());

        var independent = await handler.HandleAsync(Args(rootB, solutionB), CancellationToken.None);
        independent.IsError.Should().BeFalse();
        independent.Content.Should().Contain("\"already_existed\":false");
        await compiler.Received(1)
            .CompileAndExtractAsync(solutionB, Arg.Any<CancellationToken>());

        var storageA = SolutionScope.ToStorageRepoId(publicRepoId, currentA);
        var storageB = SolutionScope.ToStorageRepoId(publicRepoId, currentB);
        (await _store.BaselineExistsAsync(storageA, sha)).Should().BeTrue();
        (await _store.BaselineExistsAsync(storageB, sha)).Should().BeTrue();
        (await _store.GetRepoRootAsync(storageA, sha)).Should().Be(rootA);
        (await _store.GetRepoRootAsync(storageB, sha)).Should().Be(rootB);
    }

    private static string CreateSolution(string root)
    {
        var solution = Path.Combine(root, "src", "App.sln");
        Directory.CreateDirectory(Path.GetDirectoryName(solution)!);
        File.WriteAllText(solution, string.Empty);
        return solution;
    }

    private static CompilationResult EmptyCompilation(string sourcePath) =>
        new(
            Symbols: [],
            References: [],
            Files: [],
            Stats: new IndexStats(0, 0, 0, 0, Confidence.High),
            SourcePath: sourcePath);

    private static JsonObject Args(string repoPath, string solutionPath) =>
        new()
        {
            ["repo_path"] = repoPath,
            ["solution_path"] = solutionPath,
        };
}
