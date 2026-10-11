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
        // ======================== [assembly: CacheInterface] ========================

        /// <summary>
        /// One <c>[assembly: CacheInterface(typeof(IClient), "Method", ...)]</c>: the interface it names (when valid),
        /// the model for its listed methods, and where the attribute is so diagnostics point at it.
        /// </summary>
        private sealed class ExternalInterface
        {
            public ExternalInterface(INamedTypeSymbol? symbol, InterfaceInfo? info, Location? location, ImmutableArray<Diagnostic> diagnostics)
            {
                Symbol = symbol;
                Info = info;
                Location = location;
                Diagnostics = diagnostics;
            }

            public INamedTypeSymbol? Symbol { get; }

            public InterfaceInfo? Info { get; }

            public Location? Location { get; }

            public ImmutableArray<Diagnostic> Diagnostics { get; }
        }

        private static ImmutableArray<ExternalInterface> GetExternalInterfaces(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            var compilation = ctx.SemanticModel.Compilation;
            var results = ImmutableArray.CreateBuilder<ExternalInterface>(ctx.Attributes.Length);
            foreach (var attribute in ctx.Attributes)
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation();
                results.Add(GetExternalInterface(attribute, compilation, location));
            }

            return results.ToImmutable();
        }

        private static ExternalInterface GetExternalInterface(AttributeData attribute, Compilation compilation, Location? location)
        {
            if (attribute.ConstructorArguments.Length < 2 ||
                attribute.ConstructorArguments[0].Value is not INamedTypeSymbol type)
            {
                // The compiler already reports an attribute it cannot bind
                return new ExternalInterface(null, null, location, ImmutableArray<Diagnostic>.Empty);
            }

            string? problem = null;
            if (type.TypeKind != TypeKind.Interface)
            {
                problem = "it is not an interface";
            }
            else if (type.IsGenericType)
            {
                problem = "generic interfaces are not supported";
            }
            else if (!Utils.IsFromReferencedAssembly(type))
            {
                problem = "it is declared in this project; put [Cache] on its methods instead";
            }
            else if (!compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
            {
                problem = "it is not accessible from this project";
            }
            else if (Utils.HasStaticAbstractMembers(type))
            {
                problem = "it has static abstract members, so it can't be used as a service type";
            }

            var methodNames = attribute.ConstructorArguments[1].Kind == TypedConstantKind.Array
                ? attribute.ConstructorArguments[1].Values.Select(v => v.Value as string).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList()
                : new List<string?>();
            if (problem is null && methodNames.Count == 0)
            {
                problem = "list the methods to cache, for example nameof(IClient.GetAsync)";
            }

            var typeName = type.ToDisplayString();
            if (problem != null)
            {
                return new ExternalInterface(null, null, location, ImmutableArray.Create(
                    Diagnostic.Create(Diagnostics.CacheInterfaceUnsupportedType, location, typeName, problem)));
            }

            var allMethods = GetAllInterfaceMethods(type)
                .Where(m => m.MethodKind == MethodKind.Ordinary)
                .ToList();
            var cachedMethods = new List<MethodModel>();
            var diagnostics = new List<Diagnostic>();

            foreach (var name in methodNames)
            {
                var candidates = allMethods.Where(m => m.Name == name).ToList();
                if (candidates.Count != 1)
                {
                    diagnostics.Add(Diagnostic.Create(Diagnostics.CacheInterfaceMethodNotUsable, location, typeName, name,
                        candidates.Count == 0
                            ? "the interface has no method with that name"
                            : "the interface has several overloads with that name, which can't be told apart"));
                    continue;
                }

                var method = candidates[0];
                if (method.IsStatic)
                {
                    diagnostics.Add(Diagnostic.Create(Diagnostics.CacheInterfaceMethodNotUsable, location, typeName, name, "static members are not called through the instance"));
                    continue;
                }

                var validation = ValidateCacheMethod(method, location);
                if (validation.diagnostic != null)
                {
                    if (!validation.canProceed)
                    {
                        diagnostics.Add(Diagnostic.Create(Diagnostics.CacheInterfaceMethodNotUsable, location, typeName, name,
                            UncacheableReason(validation.diagnostic)));
                        continue;
                    }

                    diagnostics.Add(validation.diagnostic);
                }

                // The interface's methods live in metadata, so tag problems are reported on the attribute
                diagnostics.AddRange(ValidateDynamicTags(ExtractTags(attribute), method, location));

                // The assembly attribute carries the same named settings as [Cache], so the emitters read it as one
                cachedMethods.Add(new MethodModel(method, attribute, null));
            }

            var info = cachedMethods.Count == 0
                ? null
                : new InterfaceInfo(type, cachedMethods.ToImmutableArray(), ImmutableArray<MethodModel>.Empty, ImmutableArray<Diagnostic>.Empty);
            return new ExternalInterface(type, info, location, diagnostics.ToImmutableArray());
        }

        /// <summary>
        /// Reports the external interfaces' diagnostics, and an interface listed by more than one attribute, and
        /// returns the models to generate decorators for.
        /// </summary>
        private static IEnumerable<InterfaceInfo> ResolveExternalInterfaces(SourceProductionContext context, ImmutableArray<ExternalInterface> externals)
        {
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var external in externals)
            {
                foreach (var diagnostic in external.Diagnostics)
                {
                    context.ReportDiagnostic(diagnostic);
                }

                if (external.Symbol is null)
                {
                    continue;
                }

                if (!seen.Add(external.Symbol))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.CacheInterfaceDuplicate, external.Location, external.Symbol.ToDisplayString()));
                    continue;
                }

                if (external.Info != null)
                {
                    yield return external.Info;
                }
            }
        }

        // The [Cache] diagnostics end with "or remove [Cache]", which makes no sense for a method listed by name
        private static string UncacheableReason(Diagnostic diagnostic) => diagnostic.Id switch
        {
            "MCG001" => "it returns void, so there is nothing to cache",
            "MCG002" => "it returns a Task or ValueTask without a result, so there is nothing to cache",
            "MCG003" => "ref, out and in parameters can't be part of a cache key",
            "MCG006" => "pointer parameters can't be part of a cache key",
            "MCG007" => "ref struct parameters such as Span<T> can't be part of a cache key",
            "MCG014" => "it returns a lazy sequence, which would run again on every enumeration",
            _ => diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
