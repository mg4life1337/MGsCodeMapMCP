namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class StorageReaderCacheTests : IAsyncLifetime
{
    private const string ShaValue = "abcdef0123456789abcdef0123456789abcdef01";
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"codemap-reader-cache-{Guid.NewGuid():N}");
    private CustomSymbolStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        foreach (var repo in new[] { "repo-a", "repo-b", "repo-c" })
        {
            var builder = new EngineBaselineBuilder(Path.Combine(_tempDir, repo));
            var result = await builder.BuildAsync(TestData.CreateTestInput(), CancellationToken.None);
            result.Success.Should().BeTrue(result.ErrorMessage);
        }

        _store = new CustomSymbolStore(
            _tempDir,
            new IndexingResourceConfig(
                MaxOpenBaselineReaders: 2,
                MaxOpenOverlayReaders: 2,
                StorageReaderIdleSeconds: 1));
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BaselineLru_StaysBounded_AndEvictedReaderReopens()
    {
        var sha = CommitSha.From(ShaValue);
        foreach (var repo in new[] { "repo-a", "repo-b", "repo-c" })
        {
            var results = await _store.SearchSymbolsAsync(
                RepoId.From(repo), sha, "Foo", null, 10);
            results.Should().NotBeEmpty();
        }

        _store.OpenBaselineReaderCount.Should().Be(2);

        var reopened = await _store.SearchSymbolsAsync(
            RepoId.From("repo-a"), sha, "Foo", null, 10);
        reopened.Should().NotBeEmpty();
        _store.OpenBaselineReaderCount.Should().Be(2);
    }

    [Fact]
    public async Task ActiveBaselineLease_IsNeverClosedByIdleTrim()
    {
        using (var lease = _store.AcquireBaseline("repo-a", ShaValue))
        {
            await Task.Delay(1100);
            _store.TrimIdleReaders();

            _store.OpenBaselineReaderCount.Should().Be(1);
            lease.Reader.GetSymbolByFqn("T:MyApp.Foo").Should().NotBeNull();
        }

        await Task.Delay(1100);
        _store.TrimIdleReaders();
        _store.OpenBaselineReaderCount.Should().Be(0);
    }

    [Fact]
    public async Task ClosedOverlay_ReopensFromSnapshotAndWal_WithoutReindex()
    {
        var repo = RepoId.From("repo-a");
        var workspace = WorkspaceId.From("reader-cache-workspace");
        var sha = CommitSha.From(ShaValue);
        var overlayStore = new CustomEngineOverlayStore(_store, _tempDir);
        await overlayStore.CreateOverlayAsync(repo, workspace, sha);

        var overlayKey = CustomEngineOverlayStore.OverlayKey(repo, workspace);
        using (var lease = _store.AcquireOverlay(overlayKey, repo.Value, sha.Value))
        using (var batch = lease.Overlay.BeginBatch())
        {
            var path = "src/App/CacheReopen.cs";
            var pathId = batch.InternString(path);
            var normalizedId = batch.InternString(path.ToLowerInvariant());
            batch.UpsertFile(new FileRecord(
                lease.Overlay.NextOverlayFileIntId--,
                pathId,
                normalizedId,
                0, 0, 0, 1, 0, 0));
            await batch.CommitAsync();
        }
        var revision = await overlayStore.GetRevisionAsync(repo, workspace);
        revision.Should().BeGreaterThan(0);

        await Task.Delay(1100);
        _store.TrimIdleReaders();
        _store.OpenOverlayReaderCount.Should().Be(0);
        _store.OpenBaselineReaderCount.Should().Be(0);

        (await overlayStore.GetRevisionAsync(repo, workspace)).Should().Be(revision);
        _store.OpenOverlayReaderCount.Should().Be(1);
        _store.OpenBaselineReaderCount.Should().Be(1);
        (await _store.SearchSymbolsAsync(repo, sha, "Foo", null, 10)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task PathScopedId_ReusesValidatedLegacyOverlayAfterUpgrade()
    {
        var legacyRepo = RepoId.From("repo-a");
        var pathScopedRepo = RepoId.From("repo-a::solution::sln_path_scoped");
        var workspace = WorkspaceId.From("legacy-overlay-workspace");
        var sha = CommitSha.From(ShaValue);
        var legacyStore = new CustomEngineOverlayStore(_store, _tempDir);
        await legacyStore.CreateOverlayAsync(legacyRepo, workspace, sha);

        var legacyKey = CustomEngineOverlayStore.OverlayKey(legacyRepo, workspace);
        using (var lease = _store.AcquireOverlay(legacyKey, legacyRepo.Value, sha.Value))
        using (var batch = lease.Overlay.BeginBatch())
        {
            var path = "src/App/LegacyWal.cs";
            var pathId = batch.InternString(path);
            var normalizedId = batch.InternString(path.ToLowerInvariant());
            batch.UpsertFile(new FileRecord(
                lease.Overlay.NextOverlayFileIntId--,
                pathId,
                normalizedId,
                0, 0, 0, 1, 0, 0));
            await batch.CommitAsync();
        }
        var legacyRevision = await legacyStore.GetRevisionAsync(legacyRepo, workspace);
        legacyRevision.Should().BeGreaterThan(0);

        await Task.Delay(1100);
        _store.TrimIdleReaders();
        _store.RegisterLegacyBaselineAlias(pathScopedRepo, legacyRepo);

        var upgradedStore = new CustomEngineOverlayStore(_store, _tempDir);
        await upgradedStore.CreateOverlayAsync(pathScopedRepo, workspace, sha);

        (await upgradedStore.GetRevisionAsync(pathScopedRepo, workspace))
            .Should().Be(legacyRevision);
        _store.OverlayExists(legacyKey).Should().BeTrue();
        _store.OverlayExists(CustomEngineOverlayStore.OverlayKey(pathScopedRepo, workspace))
            .Should().BeFalse();
    }
}
