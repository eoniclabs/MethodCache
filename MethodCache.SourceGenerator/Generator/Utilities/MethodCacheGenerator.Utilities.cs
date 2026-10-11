#nullable enable

using System.Threading;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Utilities ========================
        private static class Utils
        {
            internal static readonly SymbolDisplayFormat FullyQualifiedFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType,
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included);
        
            internal static readonly SymbolDisplayFormat MethodIdFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);
        
            internal static string GetFullyQualifiedName(ITypeSymbol type)
                => type.ToDisplayString(FullyQualifiedFormat);
        
            /// <summary>
            /// The namespace of an interface's decorator: the interface's own, or MethodCache.Generated for an
            /// interface in the global namespace, such as one declared next to top-level statements.
            /// </summary>
            internal static string GetDecoratorNamespace(INamedTypeSymbol interfaceSymbol)
                => interfaceSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns
                    ? ns.ToDisplayString()
                    : "MethodCache.Generated";

            internal static string GetDecoratorFullName(INamedTypeSymbol interfaceSymbol)
                => $"global::{GetDecoratorNamespace(interfaceSymbol)}.{interfaceSymbol.Name}Decorator";

            internal static string GetMethodId(IMethodSymbol method)
                => method.ToDisplayString(MethodIdFormat);
        
            internal static bool IsCancellationToken(ITypeSymbol type)
                => type.Name == nameof(CancellationToken) &&
                   type.ContainingNamespace?.ToDisplayString() == "System.Threading";
        
            internal static bool IsTask(ITypeSymbol type, out ITypeSymbol? innerType)
            {
                if (type is INamedTypeSymbol namedType)
                {
                    if (namedType.IsGenericType &&
                        namedType.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>")
                    {
                        innerType = namedType.TypeArguments[0];
                        return true;
                    }

                    if (namedType.ToDisplayString() == "System.Threading.Tasks.Task")
                    {
                        innerType = null; // Non-generic Task is conceptually void
                        return true;
                    }
                }

                innerType = null;
                return false;
            }
        
            internal static bool IsValueTask(ITypeSymbol type, out ITypeSymbol? innerType)
            {
                if (type is INamedTypeSymbol namedType)
                {
                    if (namedType.IsGenericType &&
                        namedType.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.ValueTask<TResult>")
                    {
                        innerType = namedType.TypeArguments[0];
                        return true;
                    }

                    if (namedType.ToDisplayString() == "System.Threading.Tasks.ValueTask")
                    {
                        innerType = null; // Non-generic ValueTask is conceptually void
                        return true;
                    }
                }

                innerType = null;
                return false;
            }
        
            // Fully qualified so generated code never depends on the user's usings, and with nullable annotations
            // so the decorator's signatures match the interface it implements exactly (no CS8613/CS8614 warnings)
            private static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat.FullyQualifiedFormat
                .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

            internal static string GetReturnTypeForSignature(ITypeSymbol type) => type.ToDisplayString(SignatureFormat);
        }
    }
}

