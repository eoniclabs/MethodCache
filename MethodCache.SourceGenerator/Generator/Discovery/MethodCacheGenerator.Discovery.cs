#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator.Generator.Discovery
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Discovery & Analysis ========================
        private static Modeling.MethodCacheGenerator.InterfaceInfo? GetInterfaceInfoFromMethod(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            // The target symbol is the method, we need to get the containing interface
            if (ctx.TargetSymbol is not IMethodSymbol methodSymbol)
                return null;

            var interfaceSymbol = methodSymbol.ContainingType;
            if (interfaceSymbol?.TypeKind != TypeKind.Interface)
                return null;

            return GetInterfaceInfo(interfaceSymbol);
        }

        private static Modeling.MethodCacheGenerator.InterfaceInfo? GetInterfaceInfo(INamedTypeSymbol interfaceSymbol)
        {
            var allMethods = GetAllInterfaceMethods(interfaceSymbol).ToList();
            if (allMethods.Count == 0)
                return null;

            var cachedMethods = new List<Modeling.MethodCacheGenerator.MethodModel>();
            var invalidateMethods = new List<Modeling.MethodCacheGenerator.MethodModel>();
            var diagnostics = new List<Diagnostic>();

            foreach (var method in allMethods)
            {
                if (method.MethodKind != MethodKind.Ordinary) continue;

                var methodToCheck = method.OriginalDefinition ?? method;
                var attributes = methodToCheck.GetAttributes();

                var cacheAttr = attributes.FirstOrDefault(a => IsCacheAttribute(a.AttributeClass));
                var invalidateAttr = attributes.FirstOrDefault(a => IsCacheInvalidateAttribute(a.AttributeClass));

                if (cacheAttr == null && invalidateAttr == null) continue;

                // Check for conflicts
                if (cacheAttr != null && invalidateAttr != null)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.MethodCacheGenerator.Diagnostics.AttributeConflict,
                        method.Locations.FirstOrDefault(),
                        method.ToDisplayString()));
                    continue;
                }

                if (cacheAttr != null)
                {
                    var validation = Modeling.MethodCacheGenerator.ValidateCacheMethod(method);
                    if (validation.diagnostic != null)
                    {
                        diagnostics.Add(validation.diagnostic);
                        if (!validation.canProceed) continue;
                    }

                    // Check for sync-over-async warning
                    if (!Utilities.MethodCacheGenerator.Utils.IsTask(method.ReturnType, out _) && !Utilities.MethodCacheGenerator.Utils.IsValueTask(method.ReturnType, out _))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.MethodCacheGenerator.Diagnostics.SyncOverAsyncWarning,
                            method.Locations.FirstOrDefault(),
                            method.ToDisplayString()));
                    }

                    cachedMethods.Add(new Modeling.MethodCacheGenerator.MethodModel(method, cacheAttr, null));
                }
                else if (invalidateAttr != null)
                {
                    var tags = Modeling.MethodCacheGenerator.ExtractTags(invalidateAttr);
                    if (!tags.Any())
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.MethodCacheGenerator.Diagnostics.NoInvalidateTags,
                            method.Locations.FirstOrDefault(),
                            method.ToDisplayString()));
                    }
                    else
                    {
                        // Validate dynamic tags
                        var dynamicTagDiagnostics = Modeling.MethodCacheGenerator.ValidateDynamicTags(tags, method);
                        diagnostics.AddRange(dynamicTagDiagnostics);
                    }

                    invalidateMethods.Add(new Modeling.MethodCacheGenerator.MethodModel(method, null, invalidateAttr));
                }
            }

            if (!cachedMethods.Any() && !invalidateMethods.Any() && !diagnostics.Any())
                return null;

            return new Modeling.MethodCacheGenerator.InterfaceInfo(
                interfaceSymbol,
                cachedMethods.ToImmutableArray(),
                invalidateMethods.ToImmutableArray(),
                diagnostics.ToImmutableArray());
        }

        private static IEnumerable<IMethodSymbol> GetAllInterfaceMethods(INamedTypeSymbol interfaceSymbol)
        {
            return interfaceSymbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Concat(interfaceSymbol.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>()))
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<IMethodSymbol>();
        }

        private static bool IsCacheAttribute(INamedTypeSymbol? attr)
            => attr?.Name == "CacheAttribute" &&
               attr.ContainingNamespace?.ToDisplayString() == "MethodCache.Core";

        private static bool IsCacheInvalidateAttribute(INamedTypeSymbol? attr)
            => attr?.Name == "CacheInvalidateAttribute" &&
               attr.ContainingNamespace?.ToDisplayString() == "MethodCache.Core";
    }
}
