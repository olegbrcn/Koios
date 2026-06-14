using Microsoft.CodeAnalysis;

namespace Koios.Core;

/// <summary>
/// Custom SymbolDisplayFormats. The built-in FullyQualifiedFormat drops member
/// containers (rendering MyNs.MyClass.Prop as just Prop), so we roll our own.
/// </summary>
public static class Display
{
    public static readonly SymbolDisplayFormat Fqn = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static readonly SymbolDisplayFormat Signature = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions:
            SymbolDisplayGenericsOptions.IncludeTypeParameters |
            SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeModifiers |
            SymbolDisplayMemberOptions.IncludeAccessibility |
            SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeParamsRefOut |
            SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string KindOf(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol t)
            return t.TypeKind switch
            {
                TypeKind.Class => t.IsRecord ? "record" : "class",
                TypeKind.Struct => t.IsRecord ? "record_struct" : "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => t.TypeKind.ToString().ToLowerInvariant()
            };

        return symbol switch
        {
            IMethodSymbol m => m.MethodKind switch
            {
                MethodKind.Constructor or MethodKind.StaticConstructor => "ctor",
                MethodKind.PropertyGet or MethodKind.PropertySet => "accessor",
                _ => "method"
            },
            IPropertySymbol p => p.IsIndexer ? "indexer" : "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            INamespaceSymbol => "namespace",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
    }

    // SymbolDisplayFormat for a type's name + generic parameters only (no containers).
    private static readonly SymbolDisplayFormat TypeNameFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>A clean one-line declaration for a type (the member format renders
    /// types as just their name, which is useless in a signature field).</summary>
    public static string SignatureOf(ISymbol symbol)
    {
        if (symbol is not INamedTypeSymbol t)
            return symbol.ToDisplayString(Signature);

        var parts = new List<string> { AccessibilityOf(t) };
        if (t.IsStatic) parts.Add("static");
        if (t.TypeKind == TypeKind.Class)
        {
            if (t.IsAbstract && !t.IsStatic) parts.Add("abstract");
            if (t.IsSealed && !t.IsStatic) parts.Add("sealed");
        }
        if (t.IsReadOnly && t.TypeKind == TypeKind.Struct) parts.Add("readonly");

        parts.Add(t.TypeKind switch
        {
            TypeKind.Class => t.IsRecord ? "record" : "class",
            TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => t.TypeKind.ToString().ToLowerInvariant()
        });
        parts.Add(t.ToDisplayString(TypeNameFormat));

        var bases = new List<string>();
        if (t.BaseType is { SpecialType: not SpecialType.System_Object } bt
            && bt.SpecialType != SpecialType.System_ValueType
            && t.TypeKind == TypeKind.Class)
            bases.Add(bt.ToDisplayString(TypeNameFormat));
        bases.AddRange(t.Interfaces.Select(i => i.ToDisplayString(TypeNameFormat)));

        var sig = string.Join(' ', parts);
        return bases.Count > 0 ? $"{sig} : {string.Join(", ", bases)}" : sig;
    }

    public static string AccessibilityOf(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.Private => "private",
        Accessibility.ProtectedOrInternal => "protected_internal",
        Accessibility.ProtectedAndInternal => "private_protected",
        _ => "private"
    };
}
