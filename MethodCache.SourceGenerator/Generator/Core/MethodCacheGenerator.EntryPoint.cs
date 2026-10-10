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

            // Combine both providers
            var combinedProvider = interfaceProvider.Combine(invalidateProvider)
                .Select(static (pair, _) =>
                {
                    var combined = pair.Left.Concat(pair.Right)
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

            context.RegisterSourceOutput(combinedProvider.Combine(classProvider), static (ctx, pair) => Execute(ctx, pair.Left, pair.Right));
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<InterfaceInfo?> interfaces, ImmutableArray<INamedTypeSymbol?> classes)
        {
            if (interfaces.IsDefaultOrEmpty) return;

            var validInterfaces = interfaces
                .Where(i => i != null)
                .Cast<InterfaceInfo>()
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

            // Generate sources
            foreach (var info in validInterfaces)
            {
                var decoratorCode = DecoratorEmitter.Emit(info);
                context.AddSource($"{info.Symbol.Name}Decorator.g.cs", decoratorCode);
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



