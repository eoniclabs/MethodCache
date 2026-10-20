#nullable enable

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Core Generator ========================
        // Every step after the syntax providers carries only equatable values (see Pipeline Models), so an edit
        // that leaves the cached interfaces and the classes implementing them alone regenerates nothing, and an
        // edit to one interface regenerates only its decorator.

        /// <inheritdoc />
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Look for methods with Cache attribute and get their containing interfaces
            var interfaceProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.CacheAttribute",
                    predicate: static (node, _) => node.Parent is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => GetInterfaceFromMethod(ctx, ct))
                .Where(static info => info != null)
                .Collect();

            // Also check for CacheInvalidate attributes on methods
            var invalidateProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.CacheInvalidateAttribute",
                    predicate: static (node, _) => node.Parent is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => GetInterfaceFromMethod(ctx, ct))
                .Where(static info => info != null)
                .Collect();

            // Interfaces that only inherit cached methods, such as IUserRepository : IRepository<User>
            var derivedProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is InterfaceDeclarationSyntax { BaseList: not null },
                    transform: static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol symbol
                        ? GetDerivedInterface(symbol)
                        : null)
                .Where(static info => info != null)
                .Collect();

            // Interfaces from other assemblies, named by [assembly: CacheInterface(typeof(IClient), "Method", ...)]
            var externalProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.CacheInterfaceAttribute",
                    predicate: static (node, _) => node is CompilationUnitSyntax,
                    transform: static (ctx, ct) => GetExternalInterfaces(ctx, ct))
                .SelectMany(static (items, _) => items)
                .Collect()
                .Select(static (externals, _) => ResolveExternalInterfaces(externals))
                .WithTrackingName(TrackingNames.ExternalInterfaces);

            // One interface can be found by several providers; the external ones follow the project's own
            var interfaces = interfaceProvider.Combine(invalidateProvider).Combine(derivedProvider).Combine(externalProvider)
                .Select(static (input, _) => new EquatableArray<GeneratedInterface>(
                    input.Left.Left.Left.Concat(input.Left.Left.Right).Concat(input.Left.Right)
                        .GroupBy(i => i!.Key, System.StringComparer.Ordinal)
                        .Select(g => g.First()!)
                        .Concat(input.Right.Interfaces)))
                .WithTrackingName(TrackingNames.Interfaces);

            // Concrete classes, so AddMethodCache() can register an implementation the application did not register
            var classProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null } or RecordDeclarationSyntax { BaseList: not null },
                    transform: static (ctx, ct) => GetClassInfo(ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol))
                .Where(static info => info != null)
                .Collect()
                .Select(static (classes, _) => new EquatableArray<ClassInfo>(classes!))
                .WithTrackingName(TrackingNames.Classes);

            var diagnostics = interfaces.Combine(externalProvider)
                .Select(static (input, _) => new EquatableArray<DiagnosticInfo>(
                    input.Right.Diagnostics.Concat(input.Left.SelectMany(i => i.Diagnostics))));
            context.RegisterSourceOutput(diagnostics, static (ctx, items) =>
            {
                // Shared attribute errors have one owner when generator and analyzer run together.
                if (items.Any(static diagnostic => diagnostic.Id is "MC0004" or "MC0021" or "MC0023"))
                    ctx.AddSource("MethodCacheAttributeValidation.g.cs",
                        "// <auto-generated/>\nnamespace MethodCache.Generated { internal static class AttributeValidationOwner { } }\n");
                foreach (var diagnostic in items)
                {
                    ctx.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            });

            var decorators = interfaces
                .SelectMany(static (items, _) => items.Where(i => i.Summary.Generates).Select(i => (i.HintName, i.Source)))
                .WithTrackingName(TrackingNames.Decorators);
            context.RegisterSourceOutput(decorators, static (ctx, decorator) => ctx.AddSource(decorator.HintName, decorator.Source));

            var summaries = interfaces
                .Select(static (items, _) => new EquatableArray<InterfaceSummary>(items.Select(i => i.Summary).Where(s => s.Generates)))
                .WithTrackingName(TrackingNames.Summaries);
            context.RegisterSourceOutput(summaries, static (ctx, items) => EmitSharedSources(ctx, items));
            context.RegisterSourceOutput(summaries.Combine(classProvider), static (ctx, input) => EmitRegistrations(ctx, input.Left, input.Right));
        }

        private static void EmitSharedSources(SourceProductionContext context, EquatableArray<InterfaceSummary> interfaces)
        {
            if (interfaces.Count == 0) return;

            var policyRegistrationsCode = PolicyRegistrationsEmitter.Emit(interfaces);
            if (!string.IsNullOrEmpty(policyRegistrationsCode))
            {
                context.AddSource("GeneratedPolicyRegistrations.g.cs", policyRegistrationsCode);
            }
        }

        private static void EmitRegistrations(SourceProductionContext context, EquatableArray<InterfaceSummary> interfaces, EquatableArray<ClassInfo> classes)
        {
            if (interfaces.Count == 0) return;

            var registrationsCode = DecoratorRegistrationsEmitter.Emit(interfaces, classes);
            if (!string.IsNullOrEmpty(registrationsCode))
            {
                context.AddSource("MethodCacheDecoratorRegistrations.g.cs", registrationsCode);
            }
        }

        /// <summary>Names of pipeline steps, so tests can check that an unrelated edit leaves them cached.</summary>
        internal static class TrackingNames
        {
            public const string ExternalInterfaces = nameof(ExternalInterfaces);
            public const string Interfaces = nameof(Interfaces);
            public const string Classes = nameof(Classes);
            public const string Decorators = nameof(Decorators);
            public const string Summaries = nameof(Summaries);
        }
    }
}



