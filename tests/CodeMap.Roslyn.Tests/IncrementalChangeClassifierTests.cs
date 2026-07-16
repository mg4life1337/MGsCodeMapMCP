namespace CodeMap.Roslyn.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

public sealed class IncrementalChangeClassifierTests
{
    [Theory]
    [InlineData(LanguageNames.CSharp)]
    [InlineData(LanguageNames.VisualBasic)]
    public async Task TriviaOnlyChange_IsSemanticNoOp(string language)
    {
        var (oldDocument, newDocument) = Documents(language,
            CsOrVb(language,
                "public class Item { public int Run() => 1; }",
                "Public Class Item\nPublic Function Run() As Integer\nReturn 1\nEnd Function\nEnd Class"),
            CsOrVb(language,
                "// note\npublic class Item {  public int Run() => 1; }",
                "' note\nPublic Class Item\n  Public Function Run() As Integer\nReturn 1\nEnd Function\nEnd Class"));

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.NoOp);
    }

    [Theory]
    [InlineData(LanguageNames.CSharp)]
    [InlineData(LanguageNames.VisualBasic)]
    public async Task MethodBodyChange_IsDocumentImpact(string language)
    {
        var (oldDocument, newDocument) = Documents(language,
            CsOrVb(language,
                "public class Item { public int Run() => 1; }",
                "Public Class Item\nPublic Function Run() As Integer\nReturn 1\nEnd Function\nEnd Class"),
            CsOrVb(language,
                "public class Item { public int Run() => 2; }",
                "Public Class Item\nPublic Function Run() As Integer\nReturn 2\nEnd Function\nEnd Class"));

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.Body);
    }

    [Theory]
    [InlineData(LanguageNames.CSharp)]
    [InlineData(LanguageNames.VisualBasic)]
    public async Task PrivateSignatureChange_IsProjectImpact(string language)
    {
        var (oldDocument, newDocument) = Documents(language,
            CsOrVb(language,
                "public class Item { private void Run(int value) { } }",
                "Public Class Item\nPrivate Sub Run(value As Integer)\nEnd Sub\nEnd Class"),
            CsOrVb(language,
                "public class Item { private void Run(string value) { } }",
                "Public Class Item\nPrivate Sub Run(value As String)\nEnd Sub\nEnd Class"));

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.ProjectApi);
    }

    [Theory]
    [InlineData(LanguageNames.CSharp)]
    [InlineData(LanguageNames.VisualBasic)]
    public async Task PublicSignatureChange_IsDependencyImpact(string language)
    {
        var (oldDocument, newDocument) = Documents(language,
            CsOrVb(language,
                "public class Item { public void Run(int value) { } }",
                "Public Class Item\nPublic Sub Run(value As Integer)\nEnd Sub\nEnd Class"),
            CsOrVb(language,
                "public class Item { public void Run(string value) { } }",
                "Public Class Item\nPublic Sub Run(value As String)\nEnd Sub\nEnd Class"));

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.PublicApi);
    }

    [Theory]
    [InlineData(LanguageNames.CSharp)]
    [InlineData(LanguageNames.VisualBasic)]
    public async Task BaseTypeChange_IsHierarchyImpact(string language)
    {
        var (oldDocument, newDocument) = Documents(language,
            CsOrVb(language,
                "public class BaseA { } public class BaseB { } public class Item : BaseA { }",
                "Public Class BaseA\nEnd Class\nPublic Class BaseB\nEnd Class\nPublic Class Item\nInherits BaseA\nEnd Class"),
            CsOrVb(language,
                "public class BaseA { } public class BaseB { } public class Item : BaseB { }",
                "Public Class BaseA\nEnd Class\nPublic Class BaseB\nEnd Class\nPublic Class Item\nInherits BaseB\nEnd Class"));

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.Hierarchy);
    }

    [Theory]
    [InlineData(LanguageNames.CSharp)]
    [InlineData(LanguageNames.VisualBasic)]
    public async Task PublicPropertySetterRemoval_IsDependencyImpact(string language)
    {
        var (oldDocument, newDocument) = Documents(language,
            CsOrVb(language,
                "public class Item { public int Value { get; set; } }",
                "Public Class Item\nPublic Property Value As Integer\nEnd Class"),
            CsOrVb(language,
                "public class Item { public int Value { get; } }",
                "Public Class Item\nPublic ReadOnly Property Value As Integer\nEnd Class"));

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.PublicApi);
    }

    [Fact]
    public async Task PublicMethodConstraintChange_IsDependencyImpact()
    {
        var (oldDocument, newDocument) = Documents(LanguageNames.CSharp,
            "public class Item { public void Run<T>() where T : class { } }",
            "public class Item { public void Run<T>() where T : struct { } }");

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.PublicApi);
    }

    [Fact]
    public async Task PublicConstantValueChange_IsDependencyImpact()
    {
        var (oldDocument, newDocument) = Documents(LanguageNames.CSharp,
            "public class Item { public const int Value = 1; }",
            "public class Item { public const int Value = 2; }");

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.PublicApi);
    }

    [Fact]
    public async Task GlobalUsingChange_IsStructuralImpact()
    {
        var (oldDocument, newDocument) = Documents(LanguageNames.CSharp,
            "global using System; public class Item { }",
            "global using System.Text; public class Item { }");

        var result = await IncrementalChangeClassifier.ClassifyAsync(
            oldDocument, newDocument, CancellationToken.None);

        result.Impact.Should().Be(IncrementalChangeImpact.Structural);
    }

    private static (Document Old, Document New) Documents(
        string language,
        string oldSource,
        string newSource)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Sample", "Sample", language)
            .AddMetadataReference(projectId,
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(documentId, language == LanguageNames.CSharp ? "Item.cs" : "Item.vb",
                SourceText.From(oldSource), filePath: language == LanguageNames.CSharp ? "Item.cs" : "Item.vb");
        var oldDocument = solution.GetDocument(documentId)!;
        var newDocument = solution.WithDocumentText(documentId, SourceText.From(newSource))
            .GetDocument(documentId)!;
        return (oldDocument, newDocument);
    }

    private static string CsOrVb(string language, string csharp, string visualBasic) =>
        language == LanguageNames.CSharp ? csharp : visualBasic;
}
