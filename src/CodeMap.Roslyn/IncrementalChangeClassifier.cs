namespace CodeMap.Roslyn;

using System.Diagnostics;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal enum IncrementalChangeImpact
{
    NoOp,
    Body,
    ProjectApi,
    PublicApi,
    Hierarchy,
    Structural,
}

internal sealed record IncrementalChangeClassification(
    IncrementalChangeImpact Impact,
    TimeSpan SyntaxSemanticDiff,
    TimeSpan ApiFingerprint);

/// <summary>
/// Classifies one source edit from Roslyn syntax equivalence and declaration
/// fingerprints. Trivia-only edits never require a compilation-wide extraction.
/// </summary>
internal static class IncrementalChangeClassifier
{
    public static async Task<IncrementalChangeClassification> ClassifyAsync(
        Document oldDocument,
        Document newDocument,
        CancellationToken ct)
    {
        var syntaxTimer = Stopwatch.StartNew();
        var oldRoot = await oldDocument.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var newRoot = await newDocument.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (oldRoot is null || newRoot is null)
            return new(IncrementalChangeImpact.Structural, syntaxTimer.Elapsed, TimeSpan.Zero);

        bool syntaxEquivalent = oldRoot.IsEquivalentTo(newRoot, topLevel: false);
        syntaxTimer.Stop();
        if (syntaxEquivalent)
            return new(IncrementalChangeImpact.NoOp, syntaxTimer.Elapsed, TimeSpan.Zero);

        if (!string.Equals(
                ProjectGlobalSyntaxFingerprint(oldRoot),
                ProjectGlobalSyntaxFingerprint(newRoot),
                StringComparison.Ordinal))
        {
            syntaxTimer.Stop();
            return new(IncrementalChangeImpact.Structural, syntaxTimer.Elapsed, TimeSpan.Zero);
        }

        var fingerprintTimer = Stopwatch.StartNew();
        var oldSnapshot = await CreateSnapshotAsync(oldDocument, oldRoot, ct).ConfigureAwait(false);
        var newSnapshot = await CreateSnapshotAsync(newDocument, newRoot, ct).ConfigureAwait(false);
        fingerprintTimer.Stop();

        IncrementalChangeImpact impact =
            !oldSnapshot.Hierarchy.SetEquals(newSnapshot.Hierarchy) ? IncrementalChangeImpact.Hierarchy :
            !oldSnapshot.PublicApi.SetEquals(newSnapshot.PublicApi) ? IncrementalChangeImpact.PublicApi :
            !oldSnapshot.ProjectApi.SetEquals(newSnapshot.ProjectApi) ? IncrementalChangeImpact.ProjectApi :
            IncrementalChangeImpact.Body;

        return new(impact, syntaxTimer.Elapsed, fingerprintTimer.Elapsed);
    }

    private static async Task<DeclarationSnapshot> CreateSnapshotAsync(
        Document document,
        SyntaxNode root,
        CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null)
            return DeclarationSnapshot.Empty;

        var publicApi = new HashSet<string>(StringComparer.Ordinal);
        var projectApi = new HashSet<string>(StringComparer.Ordinal);
        var hierarchy = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var node in root.DescendantNodesAndSelf())
        {
            ct.ThrowIfCancellationRequested();
            ISymbol? symbol;
            try
            {
                symbol = model.GetDeclaredSymbol(node, ct);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (symbol is null || !IsApiDeclaration(symbol) || !seen.Add(symbol))
                continue;

            string fingerprint = ApiFingerprint(symbol);
            if (IsExternallyVisible(symbol))
                publicApi.Add(fingerprint);
            else
                projectApi.Add(fingerprint);

            if (symbol is INamedTypeSymbol type)
                hierarchy.Add(HierarchyFingerprint(type));
        }

        return new(publicApi, projectApi, hierarchy);
    }

    private static bool IsApiDeclaration(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => true,
        IMethodSymbol method => method.MethodKind is
            MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or
            MethodKind.UserDefinedOperator or MethodKind.Conversion,
        IPropertySymbol => true,
        IFieldSymbol field => !field.IsImplicitlyDeclared,
        IEventSymbol => true,
        _ => false,
    };

    private static bool IsExternallyVisible(ISymbol symbol)
    {
        for (ISymbol? current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (
                Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    private static string ApiFingerprint(ISymbol symbol)
    {
        string identity = symbol.GetDocumentationCommentId()
            ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var parts = new List<string>
        {
            identity,
            symbol.DeclaredAccessibility.ToString(),
            symbol.IsStatic.ToString(),
            symbol.IsAbstract.ToString(),
            symbol.IsVirtual.ToString(),
            symbol.IsOverride.ToString(),
            symbol.IsSealed.ToString(),
        };

        switch (symbol)
        {
            case IMethodSymbol method:
                parts.Add(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                parts.Add(method.ReturnsByRef.ToString());
                parts.Add(method.ReturnsByRefReadonly.ToString());
                AppendParameters(parts, method.Parameters);
                AppendTypeParameters(parts, method.TypeParameters);
                break;
            case IPropertySymbol property:
                parts.Add(property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                parts.Add(property.GetMethod?.DeclaredAccessibility.ToString() ?? "no-get");
                parts.Add(property.SetMethod?.DeclaredAccessibility.ToString() ?? "no-set");
                parts.Add(property.ReturnsByRef.ToString());
                parts.Add(property.ReturnsByRefReadonly.ToString());
                AppendParameters(parts, property.Parameters);
                break;
            case IFieldSymbol field:
                parts.Add(field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                parts.Add(field.IsConst.ToString());
                parts.Add(field.IsReadOnly.ToString());
                parts.Add(field.HasConstantValue ? ConstantValue(field.ConstantValue) : "no-constant");
                break;
            case IEventSymbol evt:
                parts.Add(evt.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                parts.Add(evt.AddMethod?.DeclaredAccessibility.ToString() ?? "no-add");
                parts.Add(evt.RemoveMethod?.DeclaredAccessibility.ToString() ?? "no-remove");
                break;
            case INamedTypeSymbol type:
                parts.Add(type.TypeKind.ToString());
                parts.Add(type.IsRecord.ToString());
                parts.Add(type.IsReadOnly.ToString());
                parts.Add(type.IsRefLikeType.ToString());
                parts.Add(type.EnumUnderlyingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    ?? string.Empty);
                AppendTypeParameters(parts, type.TypeParameters);
                if (type.DelegateInvokeMethod is { } invoke)
                    parts.Add(ApiFingerprint(invoke));
                break;
        }

        foreach (var attribute in symbol.GetAttributes()
            .OrderBy(AttributeFingerprint, StringComparer.Ordinal))
        {
            parts.Add(AttributeFingerprint(attribute));
        }

        return string.Join('|', parts);
    }

    private static string HierarchyFingerprint(INamedTypeSymbol type)
    {
        var parts = new List<string>
        {
            ApiFingerprint(type),
            type.BaseType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty,
        };
        parts.AddRange(type.Interfaces
            .Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .OrderBy(x => x, StringComparer.Ordinal));
        foreach (var parameter in type.TypeParameters)
        {
            var constraints = new List<string>();
            AppendTypeParameter(constraints, parameter);
            parts.AddRange(constraints);
        }
        return string.Join('|', parts);
    }

    private static void AppendParameters(List<string> parts, IEnumerable<IParameterSymbol> parameters)
    {
        foreach (var parameter in parameters)
        {
            parts.Add(string.Join(':',
                parameter.Name,
                parameter.RefKind,
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                parameter.IsParams,
                parameter.IsOptional,
                parameter.HasExplicitDefaultValue
                    ? ConstantValue(parameter.ExplicitDefaultValue)
                    : "no-default"));
        }
    }

    private static void AppendTypeParameters(
        List<string> parts,
        IEnumerable<ITypeParameterSymbol> parameters)
    {
        foreach (var parameter in parameters)
            AppendTypeParameter(parts, parameter);
    }

    private static void AppendTypeParameter(List<string> parts, ITypeParameterSymbol parameter)
    {
        parts.Add(string.Join(':',
            parameter.Name,
            parameter.Variance,
            parameter.HasReferenceTypeConstraint,
            parameter.HasValueTypeConstraint,
            parameter.HasUnmanagedTypeConstraint,
            parameter.HasNotNullConstraint,
            parameter.HasConstructorConstraint,
            string.Join(',', parameter.ConstraintTypes
                .Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .OrderBy(value => value, StringComparer.Ordinal))));
    }

    private static string AttributeFingerprint(AttributeData attribute)
    {
        var parts = new List<string>
        {
            attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                ?? string.Empty,
        };
        parts.AddRange(attribute.ConstructorArguments.Select(TypedConstantValue));
        parts.AddRange(attribute.NamedArguments
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + TypedConstantValue(pair.Value)));
        return string.Join(',', parts);
    }

    private static string TypedConstantValue(TypedConstant constant)
    {
        string type = constant.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? string.Empty;
        string value = constant.Kind == TypedConstantKind.Array
            ? "[" + string.Join(',', constant.Values.Select(TypedConstantValue)) + "]"
            : ConstantValue(constant.Value);
        return constant.Kind + ":" + type + ":" + value;
    }

    private static string ConstantValue(object? value) => value switch
    {
        null => "null",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string ProjectGlobalSyntaxFingerprint(SyntaxNode root)
    {
        var values = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(usingDirective => !usingDirective.GlobalKeyword.IsKind(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
            .Select(node => node.ToString())
            .Concat(root.DescendantNodes().OfType<AttributeListSyntax>()
                .Where(attributes => attributes.Target?.Identifier.ValueText is "assembly" or "module")
                .Select(node => node.ToString()))
            .Concat(root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.AttributesStatementSyntax>()
                .Select(node => node.ToString()))
            .OrderBy(value => value, StringComparer.Ordinal);
        return string.Join('\n', values);
    }

    private sealed record DeclarationSnapshot(
        HashSet<string> PublicApi,
        HashSet<string> ProjectApi,
        HashSet<string> Hierarchy)
    {
        public static DeclarationSnapshot Empty { get; } = new(
            new(StringComparer.Ordinal),
            new(StringComparer.Ordinal),
            new(StringComparer.Ordinal));
    }
}
