namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Query;
using FluentAssertions;

/// <summary>
/// Pure unit tests for <see cref="MergeHelpers.MergeSearchResults"/>.
/// No mocks required — the function is a deterministic pure function.
/// </summary>
public class MergeHelpersTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolSearchHit MakeHit(string id, string file, double score = 1.0) =>
        new(
            SymbolId: SymbolId.From(id),
            FullyQualifiedName: id,
            Kind: SymbolKind.Class,
            Signature: $"class {id}",
            DocumentationSnippet: null,
            FilePath: FilePath.From(file),
            Line: 1,
            Score: score);

    private static readonly IReadOnlySet<SymbolId> NoDeleted = new HashSet<SymbolId>();
    private static readonly IReadOnlySet<FilePath> NoOverlayFiles = new HashSet<FilePath>();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_EmptyOverlay_ReturnsBaselineOnly()
    {
        var baseline = new[] { MakeHit("T:Foo", "src/Foo.cs"), MakeHit("T:Bar", "src/Bar.cs") };
        var result = MergeHelpers.MergeSearchResults(baseline, [], NoDeleted, NoOverlayFiles, 10);
        result.Hits.Should().HaveCount(2);
        result.Hits.Select(h => h.SymbolId.Value).Should().BeEquivalentTo(["T:Foo", "T:Bar"]);
    }

    [Fact]
    public void Merge_EmptyBaseline_ReturnsOverlayOnly()
    {
        var overlay = new[] { MakeHit("T:New", "src/New.cs") };
        var result = MergeHelpers.MergeSearchResults([], overlay, NoDeleted, NoOverlayFiles, 10);
        result.Hits.Should().HaveCount(1);
        result.Hits[0].SymbolId.Value.Should().Be("T:New");
    }

    [Fact]
    public void Merge_BothEmpty_ReturnsEmpty()
    {
        var result = MergeHelpers.MergeSearchResults([], [], NoDeleted, NoOverlayFiles, 10);
        result.Hits.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Merge_OverlayReplacesBaselineByFile()
    {
        // File "src/A.cs" is reindexed — baseline symbol from it is superseded
        var baseline = new[] { MakeHit("T:Old", "src/A.cs"), MakeHit("T:Other", "src/B.cs") };
        var overlay = new[] { MakeHit("T:New", "src/A.cs") };
        var overlayFiles = new HashSet<FilePath> { FilePath.From("src/A.cs") };

        var result = MergeHelpers.MergeSearchResults(baseline, overlay, NoDeleted, overlayFiles, 10);

        result.Hits.Should().HaveCount(2);
        result.Hits.Select(h => h.SymbolId.Value).Should().Contain("T:New");
        result.Hits.Select(h => h.SymbolId.Value).Should().Contain("T:Other");
        result.Hits.Select(h => h.SymbolId.Value).Should().NotContain("T:Old");
    }

    [Fact]
    public void Merge_DeletedSymbolsExcluded()
    {
        var baseline = new[] { MakeHit("T:Deleted", "src/A.cs"), MakeHit("T:Kept", "src/B.cs") };
        var deleted = new HashSet<SymbolId> { SymbolId.From("T:Deleted") };

        var result = MergeHelpers.MergeSearchResults(baseline, [], deleted, NoOverlayFiles, 10);

        result.Hits.Should().HaveCount(1);
        result.Hits[0].SymbolId.Value.Should().Be("T:Kept");
    }

    [Fact]
    public void Merge_OverlayAndDeletedOverlap_OverlayWins()
    {
        // Symbol appears in overlay AND is marked deleted — overlay takes priority (it was re-added)
        // The filter applies to baseline, not overlay hits.
        var baseline = new[] { MakeHit("T:Both", "src/A.cs") };
        var overlay = new[] { MakeHit("T:Both", "src/A.cs") };
        var deleted = new HashSet<SymbolId> { SymbolId.From("T:Both") };

        var result = MergeHelpers.MergeSearchResults(baseline, overlay, deleted, NoOverlayFiles, 10);

        // Overlay hit passes through; baseline hit is excluded (deleted)
        result.Hits.Should().HaveCount(1);
        result.Hits[0].SymbolId.Value.Should().Be("T:Both");
    }

    [Fact]
    public void Merge_LimitApplied_Truncates()
    {
        var baseline = Enumerable.Range(0, 8).Select(i => MakeHit($"T:B{i}", "src/B.cs")).ToList();
        var overlay = Enumerable.Range(0, 4).Select(i => MakeHit($"T:O{i}", "src/O.cs")).ToList();

        var result = MergeHelpers.MergeSearchResults(baseline, overlay, NoDeleted, NoOverlayFiles, 5);

        result.Hits.Should().HaveCount(5);
    }

    [Fact]
    public void Merge_TruncationDetected()
    {
        var baseline = Enumerable.Range(0, 10).Select(i => MakeHit($"T:B{i}", "src/B.cs")).ToList();

        var result = MergeHelpers.MergeSearchResults(baseline, [], NoDeleted, NoOverlayFiles, 5);

        result.Truncated.Should().BeTrue();
        result.TotalCount.Should().Be(6); // limit+1
    }

    [Fact]
    public void Merge_DuplicateSymbolId_OverlayWins()
    {
        // Same symbol exists in both baseline and overlay — only overlay version appears
        var baseline = new[] { MakeHit("T:Both", "src/A.cs", score: 0.9) };
        var overlay = new[] { MakeHit("T:Both", "src/A.cs", score: 1.0) };
        var overlayFiles = new HashSet<FilePath> { FilePath.From("src/A.cs") };

        var result = MergeHelpers.MergeSearchResults(baseline, overlay, NoDeleted, overlayFiles, 10);

        result.Hits.Should().HaveCount(1); // baseline excluded by overlayFiles filter
    }

    [Fact]
    public void Merge_PreservesOverlayOrdering()
    {
        var overlay = new[]
        {
            MakeHit("T:O1", "src/O.cs", score: 0.9),
            MakeHit("T:O2", "src/O.cs", score: 0.8),
            MakeHit("T:O3", "src/O.cs", score: 0.7),
        };

        var result = MergeHelpers.MergeSearchResults([], overlay, NoDeleted, NoOverlayFiles, 10);

        result.Hits.Select(h => h.SymbolId.Value).Should().ContainInOrder("T:O1", "T:O2", "T:O3");
    }

    [Fact]
    public void Merge_BaselineSymbolFromReindexedFile_Excluded()
    {
        var baseline = new[] { MakeHit("T:FromReindexed", "src/Reindexed.cs") };
        var overlayFiles = new HashSet<FilePath> { FilePath.From("src/Reindexed.cs") };

        var result = MergeHelpers.MergeSearchResults(baseline, [], NoDeleted, overlayFiles, 10);

        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Merge_BaselineSymbolFromNonReindexedFile_Included()
    {
        var baseline = new[] { MakeHit("T:Untouched", "src/Untouched.cs") };
        var overlayFiles = new HashSet<FilePath> { FilePath.From("src/Reindexed.cs") };

        var result = MergeHelpers.MergeSearchResults(baseline, [], NoDeleted, overlayFiles, 10);

        result.Hits.Should().HaveCount(1);
        result.Hits[0].SymbolId.Value.Should().Be("T:Untouched");
    }

    [Fact]
    public void Merge_SameSymbolWithFilenameOnlyAndFullPath_ReturnsOverlayOnce()
    {
        var baseline = new[] { MakeHit("T:Same", "Item.vb") };
        var overlay = new[] { MakeHit("T:Same", "src/Feature/Item.vb") };

        var result = MergeHelpers.MergeSearchResults(
            baseline, overlay, NoDeleted, NoOverlayFiles, 10);

        result.Hits.Should().ContainSingle();
        result.Hits[0].FilePath.Value.Should().Be("src/Feature/Item.vb");
    }

    [Fact]
    public void Merge_PathSlashAndCaseVariants_AreOneAuthoritativeFile()
    {
        var baseline = new[] { MakeHit("T:Old", "src/Feature/Item.vb") };
        var overlayFiles = new HashSet<FilePath>(RepositoryPath.FilePathComparer)
        {
            FilePath.From("SRC\\FEATURE\\ITEM.VB"),
        };

        var result = MergeHelpers.MergeSearchResults(
            baseline, [], NoDeleted, overlayFiles, 10);

        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Merge_SignatureChangeWithSameSymbolId_UsesOverlaySignature()
    {
        var baseline = new[] { MakeHit("M:Api.Run", "src/Api.cs") with { Signature = "void Run()" } };
        var overlay = new[] { MakeHit("M:Api.Run", "src/Api.cs") with { Signature = "int Run()" } };

        var result = MergeHelpers.MergeSearchResults(
            baseline, overlay, NoDeleted, NoOverlayFiles, 10);

        result.Hits.Should().ContainSingle();
        result.Hits[0].Signature.Should().Be("int Run()");
    }

    [Fact]
    public void Merge_IdenticalBasenamesInDifferentDirectories_DoNotReplaceEachOther()
    {
        var baseline = new[] { MakeHit("T:First", "src/One/Item.cs") };
        var overlay = new[] { MakeHit("T:Second", "src/Two/Item.cs") };
        var overlayFiles = new HashSet<FilePath>(RepositoryPath.FilePathComparer)
        {
            FilePath.From("src/Two/Item.cs"),
        };

        var result = MergeHelpers.MergeSearchResults(
            baseline, overlay, NoDeleted, overlayFiles, 10);

        result.Hits.Select(hit => hit.SymbolId.Value).Should().Contain(["T:First", "T:Second"]);
    }

    [Fact]
    public void Merge_OverloadsWithDifferentSymbolIds_ArePreserved()
    {
        var overlay = new[]
        {
            MakeHit("M:Api.Run(System.Int32)", "src/Api.cs") with { FullyQualifiedName = "Api.Run" },
            MakeHit("M:Api.Run(System.String)", "src/Api.cs") with { FullyQualifiedName = "Api.Run" },
        };

        var result = MergeHelpers.MergeSearchResults([], overlay, NoDeleted, NoOverlayFiles, 10);

        result.Hits.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_RenamedSymbolWithSameStableIdAndProject_UsesOverlayOnce()
    {
        var baseline = new[]
        {
            MakeHit("T:OldName", "src/Old.cs") with
            {
                StableId = new StableId("sym_shared"),
                ProjectName = "ProjectA",
            },
        };
        var overlay = new[]
        {
            MakeHit("T:NewName", "src/New.cs") with
            {
                StableId = new StableId("sym_shared"),
                ProjectName = "ProjectA",
            },
        };

        var result = MergeHelpers.MergeSearchResults(
            baseline, overlay, NoDeleted, NoOverlayFiles, 10);

        result.Hits.Should().ContainSingle();
        result.Hits[0].SymbolId.Value.Should().Be("T:NewName");
    }

    [Fact]
    public void Merge_SameIdsFromDifferentProjects_RemainDistinct()
    {
        var first = MakeHit("T:Shared.Api", "src/A/Api.cs") with
        {
            StableId = new StableId("sym_shared"),
            ProjectName = "ProjectA",
        };
        var second = MakeHit("T:Shared.Api", "src/B/Api.cs") with
        {
            StableId = new StableId("sym_shared"),
            ProjectName = "ProjectB",
        };

        var result = MergeHelpers.MergeSearchResults(
            [first, second], [], NoDeleted, NoOverlayFiles, 10);

        result.Hits.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_DeduplicatesBeforeLimitAndCount()
    {
        var baseline = new[] { MakeHit("T:Same", "Item.cs") };
        var overlay = new[]
        {
            MakeHit("T:Same", "src/Item.cs"),
            MakeHit("T:Same", "src/Item.cs"),
        };

        var result = MergeHelpers.MergeSearchResults(
            baseline, overlay, NoDeleted, NoOverlayFiles, 1);

        result.Hits.Should().ContainSingle();
        result.TotalCount.Should().Be(1);
        result.Truncated.Should().BeFalse();
    }
}
