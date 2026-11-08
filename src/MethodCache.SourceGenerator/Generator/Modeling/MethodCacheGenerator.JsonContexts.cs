#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Derived types and the JsonSerializerContext passed to UseJson ========================
        // Under Native AOT the distributed cache's JSON can only write the types a JsonSerializerContext has contracts for.
        // A derived type MethodCache registers for a base type the context has, but that the context lacks, would stay in
        // memory; MC0040 names it where the context is passed to UseJson, and its code fix adds it to the context.

        /// <summary>
        /// The assembly metadata under which a project lists the derived types it registers, as <c>declared|derived</c>,
        /// so a project that references it checks them against the context it passes to UseJson.
        /// </summary>
        internal const string DerivedTypeMetadataKey = "MethodCache.DerivedType";

        /// <summary>A JsonSerializerContext passed to UseJson, and the types it has contracts for.</summary>
        internal sealed record UseJsonContext(LocationInfo? Location, string ContextName, string ContextId, EquatableArray<string> Contracts);

        private static void RegisterJsonContextCheck(IncrementalGeneratorInitializationContext context, IncrementalValueProvider<DerivedTypesModel> registrations)
        {
            var contexts = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "UseJson" },
                        ArgumentList.Arguments.Count: 1,
                    },
                    transform: static (ctx, ct) => GetUseJsonContext(ctx.SemanticModel, (InvocationExpressionSyntax)ctx.Node, ct))
                .Where(static c => c != null)
                .Collect()
                .Select(static (items, _) => new EquatableArray<UseJsonContext>(items!))
                .WithTrackingName(TrackingNames.JsonContexts);
            var referenced = context.CompilationProvider
                .Select(static (compilation, _) => ReferencedMetadata(compilation, DerivedTypeMetadataKey));

            context.RegisterSourceOutput(registrations, static (ctx, model) => EmitDerivedTypeMetadata(ctx, model.Registrations));
            context.RegisterSourceOutput(contexts.Combine(registrations).Combine(referenced), static (ctx, input) =>
            {
                foreach (var diagnostic in MissingFromJsonContexts(input.Left.Left, input.Left.Right.Registrations, input.Right))
                    ctx.ReportDiagnostic(diagnostic.ToDiagnostic());
            });
        }

        /// <summary>
        /// The context of <c>UseJson(AppJsonContext.Default)</c> or <c>UseJson(AppJsonContext.Default.Options)</c> on
        /// MethodCache's builder, with the types its <c>[JsonSerializable]</c> attributes reach.
        /// </summary>
        private static UseJsonContext? GetUseJsonContext(SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol
                {
                    Name: "UseJson",
                    ContainingType: { Name: "MethodCacheBuilder", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } },
                })
            {
                return null;
            }

            var argument = invocation.ArgumentList.Arguments[0].Expression;
            var type = model.GetTypeInfo(argument, cancellationToken).Type as INamedTypeSymbol;
            if (!IsJsonSerializerContext(type) && argument is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Options" } options)
                type = model.GetTypeInfo(options.Expression, cancellationToken).Type as INamedTypeSymbol;
            if (type == null || !IsJsonSerializerContext(type)) return null;

            var contracts = new HashSet<string>(System.StringComparer.Ordinal);
            var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute" &&
                    attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is ITypeSymbol listed)
                {
                    CollectContracts(listed, visited, contracts);
                }
            }

            return new UseJsonContext(LocationInfo.From(argument.GetLocation()), type.ToDisplayString(), type.GetDocumentationCommentId() ?? string.Empty,
                new EquatableArray<string>(contracts.OrderBy(static t => t, System.StringComparer.Ordinal)));
        }

        private static bool IsJsonSerializerContext(INamedTypeSymbol? type)
        {
            for (var current = type?.BaseType; current != null; current = current.BaseType)
            {
                if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext") return true;
            }

            return false;
        }

        /// <summary>
        /// The types System.Text.Json's source generator writes contracts for from a listed type: the type, its type
        /// arguments and elements, the types of the members it writes, and the derived types it names.
        /// </summary>
        private static void CollectContracts(ITypeSymbol type, HashSet<ITypeSymbol> visited, HashSet<string> contracts)
        {
            if (!visited.Add(type)) return;
            switch (type)
            {
                case IArrayTypeSymbol array:
                    CollectContracts(array.ElementType, visited, contracts);
                    return;
                case not INamedTypeSymbol:
                    return;
            }

            var named = (INamedTypeSymbol)type;
            contracts.Add(Utils.GetFullyQualifiedName(named));
            foreach (var argument in Utils.AllTypeArguments(named)) CollectContracts(argument, visited, contracts);
            if (IsFrameworkType(named)) return;

            foreach (var member in SerializedMemberTypes(named)) CollectContracts(member, visited, contracts);
            foreach (var attribute in named.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonDerivedTypeAttribute" &&
                    attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is ITypeSymbol derived)
                {
                    CollectContracts(derived, visited, contracts);
                }
            }
        }

        /// <summary>
        /// MC0040 for each derived type registered here or in a referenced project whose declared type a context has a
        /// contract for, and the derived type none.
        /// </summary>
        private static IEnumerable<DiagnosticInfo> MissingFromJsonContexts(EquatableArray<UseJsonContext> contexts,
            EquatableArray<DerivedTypeRegistration> registrations, EquatableArray<string> referenced)
        {
            if (contexts.Count == 0) yield break;

            var pairs = registrations.SelectMany(static r => r.DerivedFqns.Select(d => (Declared: r.DeclaredFqn, Derived: d)))
                .Concat(referenced.Select(static pair => pair.Split('|')).Where(static parts => parts.Length == 2).Select(static parts => (Declared: parts[0], Derived: parts[1])))
                .Distinct()
                .OrderBy(static pair => pair.Derived, System.StringComparer.Ordinal)
                .ToList();
            foreach (var context in contexts)
            {
                var contracts = new HashSet<string>(context.Contracts, System.StringComparer.Ordinal);
                foreach (var (declared, derived) in pairs)
                {
                    if (!contracts.Contains(declared) || contracts.Contains(derived)) continue;
                    yield return new DiagnosticInfo(Diagnostics.UnlistedDerivedType, context.Location,
                        new EquatableArray<string?>(new[] { context.ContextName, WithoutGlobal(derived), WithoutGlobal(declared) }), null)
                    {
                        Properties = new EquatableArray<string>(new[] { "Context=" + context.ContextId, "DerivedType=" + derived }),
                    };
                }
            }
        }

        private static string WithoutGlobal(string fullyQualifiedName)
            => fullyQualifiedName.StartsWith("global::", System.StringComparison.Ordinal) ? fullyQualifiedName.Substring("global::".Length) : fullyQualifiedName;

        /// <summary>The values the projects this one references write under an assembly metadata key.</summary>
        private static EquatableArray<string> ReferencedMetadata(Compilation compilation, string key)
        {
            var values = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                foreach (var attribute in assembly.GetAttributes())
                {
                    if (attribute.AttributeClass is { Name: "AssemblyMetadataAttribute" } &&
                        attribute.ConstructorArguments.Length == 2 &&
                        attribute.ConstructorArguments[0].Value as string == key &&
                        attribute.ConstructorArguments[1].Value is string value)
                    {
                        values.Add(value);
                    }
                }
            }

            return new EquatableArray<string>(values);
        }

        /// <summary>Lists the derived types the project registers, for the projects that reference it.</summary>
        private static void EmitDerivedTypeMetadata(SourceProductionContext context, EquatableArray<DerivedTypeRegistration> registrations)
        {
            if (registrations.Count == 0) return;

            var source = new System.Text.StringBuilder()
                .AppendLine("// <auto-generated/>")
                .AppendLine("// The derived types this project registers for the types cached results declare. MethodCache's generator in a")
                .AppendLine("// project that references this one checks them against the JsonSerializerContext it passes to UseJson.");
            foreach (var registration in registrations)
            {
                foreach (var derived in registration.DerivedFqns)
                {
                    source.Append("[assembly: global::System.Reflection.AssemblyMetadata(")
                        .Append(SymbolDisplay.FormatLiteral(DerivedTypeMetadataKey, quote: true))
                        .Append(", ")
                        .Append(SymbolDisplay.FormatLiteral(registration.DeclaredFqn + "|" + derived, quote: true))
                        .AppendLine(")]");
                }
            }

            context.AddSource("MethodCacheDerivedTypes.g.cs", source.ToString());
        }
    }
}
