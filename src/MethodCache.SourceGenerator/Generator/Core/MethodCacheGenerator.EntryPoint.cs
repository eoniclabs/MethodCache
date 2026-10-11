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
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Look for methods with Cache attribute and get their containing interfaces
            var interfaceProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.Core.CacheAttribute",
                    predicate: static (node, _) => node.Parent is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => GetInterfaceInfoFromMethod(ctx, ct))
                .Where(static info => info != null)
                .Collect();

            // Also check for CacheInvalidate attributes on methods
            var invalidateProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.Core.CacheInvalidateAttribute",
                    predicate: static (node, _) => node.Parent is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => GetInterfaceInfoFromMethod(ctx, ct))
                .Where(static info => info != null)
                .Collect();

            // Interfaces that only inherit cached methods, such as IUserRepository : IRepository<User>
            var derivedProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is InterfaceDeclarationSyntax { BaseList: not null },
                    transform: static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol symbol
                        ? GetInterfaceInfo(symbol)
                        : null)
                .Where(static info => info != null)
                .Collect();

            // Combine the providers
            var combinedProvider = interfaceProvider.Combine(invalidateProvider).Combine(derivedProvider)
                .Select(static (input, _) =>
                {
                    var pair = input.Left;
                    var combined = pair.Left.Concat(pair.Right).Concat(input.Right)
                        .Where(i => i != null)
                        .GroupBy(i => i!.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                        .Select(g => g.First())
                        .ToImmutableArray();
                    return combined;
                });

            // Concrete classes, so AddMethodCache() can register an implementation the application did not register
            var classProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null } or RecordDeclarationSyntax { BaseList: not null },
                    transform: static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol)
                .Where(static symbol => symbol is { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsGenericType: false })
                .Collect();

            // Interfaces from other assemblies, named by [assembly: CacheInterface(typeof(IClient), "Method", ...)]
            var externalProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.Core.CacheInterfaceAttribute",
                    predicate: static (node, _) => node is CompilationUnitSyntax,
                    transform: static (ctx, ct) => GetExternalInterfaces(ctx, ct))
                .SelectMany(static (items, _) => items)
                .Collect();

            context.RegisterSourceOutput(
                combinedProvider.Combine(externalProvider).Combine(classProvider),
                static (ctx, input) => Execute(ctx, input.Left.Left, input.Left.Right, input.Right));
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<InterfaceInfo?> interfaces, ImmutableArray<ExternalInterface> externals, ImmutableArray<INamedTypeSymbol?> classes)
        {
            var validInterfaces = interfaces
                .Where(i => i != null)
                .Cast<InterfaceInfo>()
                .Concat(ResolveExternalInterfaces(context, externals).ToList())
                .ToList();

            if (validInterfaces.Count == 0) return;

            // Report diagnostics
            foreach (var info in validInterfaces)
            {
                foreach (var diagnostic in info.Diagnostics)
                {
                    context.ReportDiagnostic(diagnostic);
                }
            }

            // An interface whose every method was rejected only reports its diagnostics
            validInterfaces = validInterfaces.Where(i => i.CachedMethods.Length > 0 || i.InvalidateMethods.Length > 0).ToList();
            if (validInterfaces.Count == 0) return;

            // Generate sources
            foreach (var info in validInterfaces)
            {
                var decoratorCode = DecoratorEmitter.Emit(info);
                context.AddSource(Utils.GetDecoratorHintName(info.Symbol), decoratorCode);
            }

            // Generate shared components
            var policyRegistrationsCode = PolicyRegistrationsEmitter.Emit(validInterfaces);
            if (!string.IsNullOrEmpty(policyRegistrationsCode))
            {
                context.AddSource("GeneratedPolicyRegistrations.g.cs", policyRegistrationsCode);
            }

            var extensionsCode = DIExtensionsEmitter.Emit(validInterfaces);
            context.AddSource("MethodCacheServiceCollectionExtensions.g.cs", extensionsCode);

            var registrationsCode = DecoratorRegistrationsEmitter.Emit(validInterfaces, classes);
            if (!string.IsNullOrEmpty(registrationsCode))
            {
                context.AddSource("MethodCacheDecoratorRegistrations.g.cs", registrationsCode);
            }
        }
    }
}



