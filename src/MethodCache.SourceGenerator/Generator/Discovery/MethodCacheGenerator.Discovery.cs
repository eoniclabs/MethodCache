#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Discovery & Analysis ========================
        private static GeneratedInterface? GetInterfaceFromMethod(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            // The target symbol is the method, we need to get the containing interface
            if (ctx.TargetSymbol is not IMethodSymbol methodSymbol)
                return null;

            var interfaceSymbol = methodSymbol.ContainingType;
            if (interfaceSymbol?.TypeKind != TypeKind.Interface)
                return null;

            // This transform runs for every attributed method on every edit. The decorator covers the whole interface,
            // so only its first attributed method builds it, not each of them again.
            if (!SymbolEqualityComparer.Default.Equals(methodSymbol, FirstAttributedMethod(interfaceSymbol)))
                return null;

            return GetGeneratedInterface(interfaceSymbol);
        }

        /// <summary>The interface's first own method with [Cache] or [CacheInvalidate], if any.</summary>
        private static IMethodSymbol? FirstAttributedMethod(INamedTypeSymbol interfaceSymbol)
            => interfaceSymbol.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(HasCacheAttribute);

        private static bool HasCacheAttribute(IMethodSymbol method)
            => method.GetAttributes().Any(a => IsCacheAttribute(a.AttributeClass) || IsCacheInvalidateAttribute(a.AttributeClass));

        /// <summary>
        /// The decorator of an interface that declares no attributed method itself but inherits some, such as
        /// IUserRepository : IRepository&lt;User&gt;. One with attributed methods of its own is built from them.
        /// </summary>
        private static GeneratedInterface? GetDerivedInterface(INamedTypeSymbol interfaceSymbol)
            => interfaceSymbol.TypeKind == TypeKind.Interface &&
               FirstAttributedMethod(interfaceSymbol) == null &&
               interfaceSymbol.AllInterfaces.Any(i => i.GetMembers().OfType<IMethodSymbol>().Any(m => HasCacheAttribute(m.OriginalDefinition)))
                ? GetGeneratedInterface(interfaceSymbol)
                : null;

        private static GeneratedInterface? GetGeneratedInterface(INamedTypeSymbol interfaceSymbol)
            => GetInterfaceInfo(interfaceSymbol) is { } info ? ToGeneratedInterface(info) : null;

        /// <summary>
        /// Generates the interface's decorator and keeps only values from its symbols, so the pipeline can tell
        /// when nothing changed.
        /// </summary>
        private static GeneratedInterface ToGeneratedInterface(InterfaceInfo info)
        {
            var symbol = info.Symbol;
            var summary = new InterfaceSummary(
                symbol.Name,
                GetAssemblyIdentity(symbol),
                Utils.GetFullyQualifiedName(symbol),
                Utils.GetFullyQualifiedName(symbol.OriginalDefinition),
                Utils.GetDecoratorFullName(symbol),
                symbol.IsGenericType,
                info.CachedMethods.Length > 0,
                info.InvalidateMethods.Length > 0,
                PolicyRegistrationsEmitter.BuildEntries(info));

            return new GeneratedInterface(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                summary,
                Utils.GetDecoratorHintName(symbol),
                summary.Generates ? DecoratorEmitter.Emit(info) : string.Empty,
                new EquatableArray<DiagnosticInfo>(info.Diagnostics));
        }

        private static string GetAssemblyIdentity(INamedTypeSymbol type) => type.ContainingAssembly?.Identity.ToString() ?? string.Empty;

        /// <summary>A class that implements interfaces, as the decorator registrations need it.</summary>
        private static ClassInfo? GetClassInfo(INamedTypeSymbol? symbol)
        {
            if (symbol is not { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsGenericType: false } || symbol.AllInterfaces.Length == 0)
            {
                return null;
            }

            var interfaces = symbol.AllInterfaces
                .Select(i => new ImplementedInterface(
                    GetAssemblyIdentity(i),
                    Utils.GetFullyQualifiedName(i),
                    Utils.GetFullyQualifiedName(i.OriginalDefinition),
                    i.ToDisplayString(),
                    new EquatableArray<string>(Utils.AllTypeArguments(i).Select(Utils.GetFullyQualifiedName)),
                    Utils.AllTypeArguments(i).All(t => DecoratorRegistrationsEmitter.IsNameableFrom(t, symbol.ContainingAssembly))));

            return new ClassInfo(
                Utils.GetFullyQualifiedName(symbol),
                symbol.Name,
                symbol.ToDisplayString(),
                DecoratorRegistrationsEmitter.IsAccessibleFromAssembly(symbol),
                symbol.InstanceConstructors.Any(ctor => ctor.DeclaredAccessibility == Accessibility.Public),
                new EquatableArray<ImplementedInterface>(interfaces));
        }

        private static InterfaceInfo? GetInterfaceInfo(INamedTypeSymbol interfaceSymbol)
        {
            if (Utils.HasStaticAbstractMembers(interfaceSymbol) || Utils.HasNonPublicAbstractMembers(interfaceSymbol))
            {
                // Reported once; Execute emits nothing for an interface without cached or invalidating methods
                return new InterfaceInfo(
                    interfaceSymbol,
                    ImmutableArray<MethodModel>.Empty,
                    ImmutableArray<MethodModel>.Empty,
                    ImmutableArray.Create(DiagnosticInfo.Create(
                        Diagnostics.UnsupportedInterface,
                        interfaceSymbol.Locations.FirstOrDefault(),
                        interfaceSymbol.ToDisplayString(),
                        Utils.HasStaticAbstractMembers(interfaceSymbol)
                            ? "it has static abstract members, so it can't be used as a service type"
                            : "it has non-public abstract members that cannot be forwarded to the decorated instance")));
            }

            var allMethods = GetAllInterfaceMethods(interfaceSymbol).ToList();
            if (allMethods.Count == 0)
                return null;

            var cachedMethods = new List<MethodModel>();
            var invalidateMethods = new List<MethodModel>();
            var diagnostics = new List<DiagnosticInfo>();

            foreach (var method in allMethods)
            {
                if (method.MethodKind != MethodKind.Ordinary) continue;

                var methodToCheck = method.OriginalDefinition ?? method;
                var attributes = methodToCheck.GetAttributes();

                var cacheAttr = attributes.FirstOrDefault(a => IsCacheAttribute(a.AttributeClass));
                var invalidateAttr = attributes.FirstOrDefault(a => IsCacheInvalidateAttribute(a.AttributeClass));

                if (cacheAttr == null && invalidateAttr == null) continue;

                // The decorator implements only the members a class can implement; anything else would keep its
                // attribute and silently never be cached or invalidate
                if (!Utils.IsInterceptable(method))
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        Diagnostics.NotInterceptableMember,
                        method.Locations.FirstOrDefault(),
                        cacheAttr != null ? "Cache" : "CacheInvalidate",
                        method.ToDisplayString(),
                        method.IsStatic ? "static" : method.DeclaredAccessibility != Accessibility.Public ? "not public" : "sealed"));
                    continue;
                }

                // Check for conflicts
                if (cacheAttr != null && invalidateAttr != null)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        Diagnostics.AttributeConflict,
                        method.Locations.FirstOrDefault(),
                        method.ToDisplayString()));
                    continue;
                }

                if (cacheAttr != null)
                {
                    var validation = ValidateCacheMethod(method);
                    if (validation.diagnostic != null)
                    {
                        diagnostics.Add(validation.diagnostic);
                        if (!validation.canProceed) continue;
                    }

                    // A typo in a {parameter} placeholder would otherwise stay literal text in every entry's tag
                    diagnostics.AddRange(ValidateDynamicTags(ExtractTags(cacheAttr), method));

                    cachedMethods.Add(new MethodModel(method, cacheAttr, null));
                }
                else if (invalidateAttr != null)
                {
                    var tags = ExtractTags(invalidateAttr);
                    if (!tags.Any())
                    {
                        diagnostics.Add(DiagnosticInfo.Create(
                            Diagnostics.NoInvalidateTags,
                            method.Locations.FirstOrDefault(),
                            method.ToDisplayString()));
                    }
                    else
                    {
                        // Validate dynamic tags
                        var dynamicTagDiagnostics = ValidateDynamicTags(tags, method);
                        diagnostics.AddRange(dynamicTagDiagnostics);
                    }

                    invalidateMethods.Add(new MethodModel(method, null, invalidateAttr));
                }
            }

            if (!cachedMethods.Any() && !invalidateMethods.Any() && !diagnostics.Any())
                return null;

            return new InterfaceInfo(
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
               attr.ContainingNamespace?.ToDisplayString() == "MethodCache";

        private static bool IsCacheInvalidateAttribute(INamedTypeSymbol? attr)
            => attr?.Name == "CacheInvalidateAttribute" &&
               attr.ContainingNamespace?.ToDisplayString() == "MethodCache";
    }
}
