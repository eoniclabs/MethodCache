#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Models ========================
        private sealed class InterfaceInfo
        {
            public INamedTypeSymbol Symbol { get; }
            public ImmutableArray<MethodModel> CachedMethods { get; }
            public ImmutableArray<MethodModel> InvalidateMethods { get; }
            public ImmutableArray<Diagnostic> Diagnostics { get; }

            public InterfaceInfo(
                INamedTypeSymbol symbol,
                ImmutableArray<MethodModel> cachedMethods,
                ImmutableArray<MethodModel> invalidateMethods,
                ImmutableArray<Diagnostic> diagnostics)
            {
                Symbol = symbol;
                CachedMethods = cachedMethods;
                InvalidateMethods = invalidateMethods;
                Diagnostics = diagnostics;
            }
        }

        private sealed class MethodModel
        {
            public IMethodSymbol Method { get; }
            public AttributeData? CacheAttr { get; }
            public AttributeData? InvalidateAttr { get; }

            public MethodModel(IMethodSymbol method, AttributeData? cacheAttr, AttributeData? invalidateAttr)
            {
                Method = method;
                CacheAttr = cacheAttr;
                InvalidateAttr = invalidateAttr;
            }
        }
    }
}
