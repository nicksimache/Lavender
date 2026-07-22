using Microsoft.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Lavender.Infrastructure.Indexing.Symbol;

/// <summary>Creates the canonical symbol IDs used by every Lavender index.</summary>
public sealed class SymbolIdentityService
{
    public static readonly SymbolDisplayFormat IdentityFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public string GetId(ISymbol symbol)
    {
        ISymbol canonical = symbol is IMethodSymbol { ReducedFrom: not null } method
            ? method.ReducedFrom.OriginalDefinition
            : symbol.OriginalDefinition;
        string assembly = canonical.ContainingAssembly?.Identity.ToString() ?? "source";
        string identity = $"{canonical.Kind}|{assembly}|{canonical.ToDisplayString(IdentityFormat)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public string GetDisplayName(ISymbol symbol) => symbol.ToDisplayString(IdentityFormat);
}
