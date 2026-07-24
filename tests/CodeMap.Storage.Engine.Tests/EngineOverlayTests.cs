namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

public sealed class EngineOverlayTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-overlay-test-{Guid.NewGuid():N}");
    private EngineBaselineReader _reader = null!;
    private string _overlayDir = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeDir = Path.Combine(_tempDir, "store");
        var builder = new EngineBaselineBuilder(storeDir);

        var input = TestData.CreateTestInput();
        var result = await builder.BuildAsync(input, CancellationToken.None);
        result.Success.Should().BeTrue();

        _reader = new EngineBaselineReader(result.BaselinePath);
        _reader.InitSearch(new SearchIndexReader(_reader, Path.Combine(result.BaselinePath, "search.idx")));
        _reader.InitAdjacency(new AdjacencyIndexReader(
            Path.Combine(result.BaselinePath, "adjacency-out.idx"),
            Path.Combine(result.BaselinePath, "adjacency-in.idx"),
            _reader.SymbolCount));
        _overlayDir = Path.Combine(storeDir, "overlays", "test-ws");
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Create_WritesManifest()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        File.Exists(Path.Combine(_overlayDir, "manifest.json")).Should().BeTrue();
        overlay.Revision.Should().Be(0);
        overlay.NBaselineStringIds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ForkSnapshot_IsRevisionExactAndIsolated()
    {
        using var source = new EngineOverlay(_overlayDir, "source", _reader);
        var stableId = source.InternStringInternal("sym_seed");
        var fqn = source.InternStringInternal("T:Seed.Type");
        using (var batch = source.BeginBatch())
        {
            batch.UpsertSymbol(
                new SymbolRecord(
                    -1, stableId, fqn, 0, 0, 0, 0, 0,
                    1, 7, 0, 1, 1, 0, 0),
                ["seed"]);
            await batch.CommitAsync();
        }

        var targetDirectory = Path.Combine(
            Path.GetDirectoryName(_overlayDir)!,
            "forked-ws");
        source.ForkSnapshot(targetDirectory, "forked-ws", expectedRevision: 1);

        using var target = new EngineOverlay(targetDirectory, "forked-ws", _reader);
        target.Revision.Should().Be(1);
        target.TryGetOverlaySymbol("sym_seed", out _).Should().NotBeNull();

        var targetStableId = target.InternStringInternal("sym_target_only");
        using (var batch = target.BeginBatch())
        {
            batch.UpsertSymbol(
                new SymbolRecord(
                    -2, targetStableId, fqn, 0, 0, 0, 0, 0,
                    1, 7, 0, 1, 1, 0, 0),
                ["target"]);
            await batch.CommitAsync();
        }

        target.Revision.Should().Be(2);
        source.Revision.Should().Be(1);
        source.TryGetOverlaySymbol("sym_target_only", out _).Should().BeNull();
    }

    [Fact]
    public void ForkSnapshot_RejectsRevisionMismatchWithoutTarget()
    {
        using var source = new EngineOverlay(_overlayDir, "source", _reader);
        var targetDirectory = Path.Combine(
            Path.GetDirectoryName(_overlayDir)!,
            "rejected-ws");

        var act = () => source.ForkSnapshot(
            targetDirectory,
            "rejected-ws",
            expectedRevision: 99);

        act.Should().Throw<InvalidOperationException>().WithMessage("*revision changed*");
        Directory.Exists(targetDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task UpsertSymbol_ThenQuery_ReturnsIt()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var stableIdSid = overlay.InternStringInternal("sym_overlay_0001");
        var fqnSid = overlay.InternStringInternal("T:MyApp.NewClass");
        var displaySid = overlay.InternStringInternal("NewClass");
        var tokensSid = overlay.InternStringInternal("newclass new class");

        var sym = new SymbolRecord(-1, stableIdSid, fqnSid, displaySid, 0, 0, 0, 0, 1, 7, 0, 1, 10, tokensSid, 0);

        using var batch = overlay.BeginBatch();
        batch.UpsertSymbol(sym, ["newclass", "new", "class"]);
        await batch.CommitAsync();

        overlay.Revision.Should().Be(1);
        var found = overlay.TryGetOverlaySymbol("sym_overlay_0001", out var tombstoned);
        found.Should().NotBeNull();
        tombstoned.Should().BeFalse();
        found!.Value.SymbolIntId.Should().Be(-1);
    }

    [Fact]
    public async Task SameStableIdInDifferentProjects_PreservesBothSymbols()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        int stableIdSid = overlay.InternStringInternal("sym_shared_projects");
        int firstFqnSid = overlay.InternStringInternal("T:First.Api");
        int secondFqnSid = overlay.InternStringInternal("T:Second.Api");

        using var batch = overlay.BeginBatch();
        batch.UpsertSymbol(new SymbolRecord(
            -1, stableIdSid, firstFqnSid, 0, 0, 0, 0, 1,
            1, 7, 0, 1, 1, 0, 0), ["api"]);
        batch.UpsertSymbol(new SymbolRecord(
            -2, stableIdSid, secondFqnSid, 0, 0, 0, 0, 2,
            1, 7, 0, 1, 1, 0, 0), ["api"]);
        await batch.CommitAsync();

        overlay.GetOverlayNewSymbols().Should().HaveCount(2);
        overlay.GetOverlaySymbolsForTokenPrefix("api").Should().HaveCount(2);
    }

    [Fact]
    public async Task Tombstone_HidesSymbol()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);

        // Get a baseline symbol's StableId
        var baselineSym = _reader.GetSymbolByFqn("T:MyApp.Foo");
        baselineSym.Should().NotBeNull();
        var stableId = _reader.ResolveString(baselineSym!.Value.StableIdStringId);

        using var batch = overlay.BeginBatch();
        batch.Tombstone(0, baselineSym.Value.SymbolIntId, stableId);
        await batch.CommitAsync();

        overlay.TryGetOverlaySymbol(stableId, out var tombstoned);
        tombstoned.Should().BeTrue();
        overlay.Tombstones.Should().Contain(stableId);
    }

    [Fact]
    public async Task AddEdge_VisibleInOutgoing()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var edge = new EdgeRecord(-1, 1, 2, 0, 1, 100, 110, 1, 0, 0, 1);

        using var batch = overlay.BeginBatch();
        batch.AddEdge(edge);
        await batch.CommitAsync();

        overlay.GetOverlayOutgoingEdges(1).Count.Should().Be(1);
    }

    [Fact]
    public void BatchDispose_WithoutCommit_NoChanges()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var stableIdSid = overlay.InternStringInternal("sym_discarded");
        var sym = new SymbolRecord(-1, stableIdSid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);

        using (var batch = overlay.BeginBatch())
        {
            batch.UpsertSymbol(sym, []);
            // Dispose without commit
        }

        overlay.Revision.Should().Be(0);
    }

    [Fact]
    public void InternString_ReturnsOverlayIds()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var id = overlay.InternStringInternal("overlay_string");
        id.Should().BeGreaterThan(overlay.NBaselineStringIds);
        overlay.ResolveString(id).Should().Be("overlay_string");
    }

    [Fact]
    public async Task Reopen_RecoverFromWal()
    {
        // Write some data, close, reopen — should recover
        {
            using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
            var stableIdSid = overlay.InternStringInternal("sym_persisted");
            var fqnSid = overlay.InternStringInternal("T:Persisted");
            var sym = new SymbolRecord(-1, stableIdSid, fqnSid, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);

            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();
        }

        // Reopen
        using var overlay2 = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var found = overlay2.TryGetOverlaySymbol("sym_persisted", out _);
        found.Should().NotBeNull();
    }

    [Fact]
    public async Task Checkpoint_ThenReopen_StatePreserved()
    {
        {
            using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
            var stableIdSid = overlay.InternStringInternal("sym_checkpoint");
            var sym = new SymbolRecord(-2, stableIdSid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);

            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();

            await overlay.DoCheckpointAsync();
        }

        File.Exists(Path.Combine(_overlayDir, "overlay.snapshot")).Should().BeTrue();

        using var overlay2 = new EngineOverlay(_overlayDir, "test-ws", _reader);
        overlay2.TryGetOverlaySymbol("sym_checkpoint", out _).Should().NotBeNull();
    }

    [Fact]
    public async Task MergedReader_TombstoneExcludesBaseline()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var baselineSym = _reader.GetSymbolByFqn("T:MyApp.Foo")!;
        var stableId = _reader.ResolveString(baselineSym.Value.StableIdStringId);

        using var batch = overlay.BeginBatch();
        batch.Tombstone(0, baselineSym.Value.SymbolIntId, stableId);
        await batch.CommitAsync();

        var merged = new EngineMergedReader(_reader, overlay);
        merged.GetSymbolByStableId(stableId).Should().BeNull();
        merged.GetSymbolByFqn("T:MyApp.Foo").Should().BeNull();
    }

    [Fact]
    public async Task MergedReader_OverlayEdgesMergedWithBaseline()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var doWork = _reader.GetSymbolByFqn("M:MyApp.Foo.DoWork")!;

        var overlayEdge = new EdgeRecord(-1, doWork.Value.SymbolIntId, 3, 0, 1, 200, 210, 1, 0, 0, 1);
        using var batch = overlay.BeginBatch();
        batch.AddEdge(overlayEdge);
        await batch.CommitAsync();

        var merged = new EngineMergedReader(_reader, overlay);
        var edges = merged.GetOutgoingEdges(doWork.Value.SymbolIntId);
        edges.Count.Should().BeGreaterThan(1); // baseline + overlay
    }

    [Fact]
    public async Task GetFilePathsSnapshot_WithEmptyPath_IncludesEmptyKey()
    {
        // Simulate an empty file path entering the overlay (from WAL corruption or orphaned string ID)
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var emptyPathSid = overlay.InternStringInternal("");
        var fileRecord = new FileRecord(-1, emptyPathSid, emptyPathSid, 0, 0, 0, 0, 0, 0);

        using var batch = overlay.BeginBatch();
        batch.UpsertFile(fileRecord);
        await batch.CommitAsync();

        var snapshot = overlay.GetFilePathsSnapshot();
        snapshot.Should().Contain(""); // Empty key is present in raw snapshot

        // The fix in CustomEngineOverlayStore.GetOverlayFilePathsAsync filters these out
        // before calling FilePath.From(), preventing ArgumentException
        var filtered = snapshot.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        filtered.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFilePathsSnapshot_MixedValidAndEmpty_FilterRetainsValid()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);

        // Add a valid file
        var validPathSid = overlay.InternStringInternal("src/App.cs");
        var validNormSid = overlay.InternStringInternal("src/app.cs");
        var validFile = new FileRecord(-1, validPathSid, validNormSid, 0, 0, 0, 0, 0, 0);

        // Add an empty file (corruption scenario)
        var emptyPathSid = overlay.InternStringInternal("");
        var emptyFile = new FileRecord(-2, emptyPathSid, emptyPathSid, 0, 0, 0, 0, 0, 0);

        using var batch = overlay.BeginBatch();
        batch.UpsertFile(validFile);
        batch.UpsertFile(emptyFile);
        await batch.CommitAsync();

        var snapshot = overlay.GetFilePathsSnapshot();
        snapshot.Should().HaveCount(2);

        var filtered = snapshot.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        filtered.Should().ContainSingle().Which.Should().Be("src/App.cs");
    }

    [Fact]
    public async Task ReplaceFile_RemovesPriorSymbolsEdgesFactsAndWalState()
    {
        const string path = "src/Changed.cs";
        {
            using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
            int pathSid = overlay.InternStringInternal(path);
            int oldStableSid = overlay.InternStringInternal("sym_old_revision");
            int oldFqnSid = overlay.InternStringInternal("T:OldRevision");
            int oldTokensSid = overlay.InternStringInternal("old revision");

            using (var first = overlay.BeginBatch())
            {
                first.UpsertFile(new FileRecord(-1, pathSid, pathSid, 0, 0, 0, 0, 0, 0));
                first.UpsertSymbol(new SymbolRecord(
                    -1, oldStableSid, oldFqnSid, 0, 0, 0, -1, 0,
                    1, 7, 0, 1, 3, oldTokensSid, 0), ["old", "revision"]);
                first.AddEdge(new EdgeRecord(-1, -1, 1, 0, -1, 2, 2, 1, 0, 0, 1));
                first.AddFact(new FactRecord(-1, -1, -1, 2, 2, 1, oldFqnSid, 0, 3, 0));
                await first.CommitAsync();
            }

            int newStableSid = overlay.InternStringInternal("sym_new_revision");
            int newFqnSid = overlay.InternStringInternal("T:NewRevision");
            using var second = overlay.BeginBatch();
            second.ReplaceFile(path);
            second.UpsertFile(new FileRecord(-2, pathSid, pathSid, 0, 0, 0, 0, 0, 0));
            second.UpsertSymbol(new SymbolRecord(
                -2, newStableSid, newFqnSid, 0, 0, 0, -2, 0,
                1, 7, 0, 1, 3, 0, 0), ["new"]);
            await second.CommitAsync();

            overlay.TryGetOverlaySymbol("sym_old_revision", out _).Should().BeNull();
            overlay.TryGetOverlaySymbol("sym_new_revision", out _).Should().NotBeNull();
            overlay.GetOverlayOutgoingEdges(-1).Should().BeEmpty();
            overlay.GetOverlayFacts(-1).Should().BeEmpty();
            overlay.GetOverlaySymbolsForTokenPrefix("old").Should().BeEmpty();
        }

        using var reopened = new EngineOverlay(_overlayDir, "test-ws", _reader);
        reopened.TryGetOverlaySymbol("sym_old_revision", out _).Should().BeNull();
        reopened.TryGetOverlaySymbol("sym_new_revision", out _).Should().NotBeNull();
        reopened.GetOverlayOutgoingEdges(-1).Should().BeEmpty();
    }
}

/// <summary>Shared test data factory.</summary>
internal static class TestData
{
    public static BaselineBuildInput CreateTestInput()
    {
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/App/Foo.cs"), "aa" + new string('0', 62), "MyApp", "public class Foo { public void DoWork() { } }"),
            new("f2", FilePath.From("src/App/Bar.cs"), "bb" + new string('0', 62), "MyApp", "public class Bar { public int Process(string x) { return 0; } }"),
            new("f3", FilePath.From("src/App/IService.cs"), "cc" + new string('0', 62), "MyApp", "public interface IService { void Run(); }"),
        };

        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.Foo"), "global::MyApp.Foo", SymbolKind.Class,
                "public class Foo", "MyApp", FilePath.From("src/App/Foo.cs"), 1, 10, "public", Confidence.High),
            SymbolCard.CreateMinimal(SymbolId.From("M:MyApp.Foo.DoWork"), "global::MyApp.Foo.DoWork", SymbolKind.Method,
                "public void DoWork()", "MyApp", FilePath.From("src/App/Foo.cs"), 3, 8, "public", Confidence.High, containingType: "Foo"),
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.Bar"), "global::MyApp.Bar", SymbolKind.Class,
                "public class Bar", "MyApp", FilePath.From("src/App/Bar.cs"), 1, 5, "public", Confidence.High),
            SymbolCard.CreateMinimal(SymbolId.From("M:MyApp.Bar.Process"), "global::MyApp.Bar.Process", SymbolKind.Method,
                "public int Process(string x)", "MyApp", FilePath.From("src/App/Bar.cs"), 2, 4, "internal", Confidence.High, containingType: "Bar"),
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.IService"), "global::MyApp.IService", SymbolKind.Interface,
                "public interface IService", "MyApp", FilePath.From("src/App/IService.cs"), 1, 3, "public", Confidence.High),
        };

        var refs = new List<ExtractedReference>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), SymbolId.From("M:MyApp.Bar.Process"), RefKind.Call, FilePath.From("src/App/Foo.cs"), 5, 5),
            new(SymbolId.From("T:MyApp.Foo"), SymbolId.From("T:MyApp.IService"), RefKind.Implementation, FilePath.From("src/App/Foo.cs"), 1, 1),
        };

        var facts = new List<ExtractedFact>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), null, FactKind.Route, "GET|/api/foo", FilePath.From("src/App/Foo.cs"), 4, 4, Confidence.High),
        };

        return new BaselineBuildInput("abcdef0123456789abcdef0123456789abcdef01", @"C:\repo", symbols, files, refs, facts, []);
    }
}
