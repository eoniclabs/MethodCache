#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using MethodCache.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        /// <summary>
        /// Writes the classes of the project whose constructors take per-request state, by the interfaces they implement,
        /// into its assembly, so MC0026 in a project that references it follows a cached implementation's interface to
        /// them. A class is checked as MC0026 checks a dependency: its own constructors, and the constructors of the
        /// classes they take. Each class is read on its own, so an edit elsewhere regenerates nothing.
        /// </summary>
        private static void RegisterRequestState(IncrementalGeneratorInitializationContext context)
        {
            var values = context.SyntaxProvider
                .CreateSyntaxProvider(
                    // Classes and record classes; a struct is copied, so it isn't the shared service MC0026 is about
                    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null }
                        or RecordDeclarationSyntax { BaseList: not null, ClassOrStructKeyword.RawKind: not (int)SyntaxKind.StructKeyword },
                    transform: static (ctx, ct) => RequestStateClassOf(ctx.SemanticModel, (TypeDeclarationSyntax)ctx.Node, ct))
                .Where(static candidate => candidate != null)
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Select(static (input, _) => RequestStateValues(input.Left!, input.Right))
                .Collect()
                .Select(static (items, _) => new EquatableArray<string>(items.SelectMany(v => v).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)))
                .WithTrackingName(TrackingNames.RequestState);
            context.RegisterSourceOutput(values, static (ctx, items) => EmitRequestState(ctx, items));
        }

        /// <summary>
        /// A class with the interfaces another project can take it by, and each type its constructors take, then each type
        /// the constructors of those classes take, with the names each type answers to. Which of them is request state is
        /// decided with the .editorconfig option of the class's file.
        /// </summary>
        internal sealed record RequestStateClass(SyntaxTree Tree, EquatableArray<string> Interfaces, string Class, EquatableArray<RequestStateEdge> Edges);

        /// <summary>A type that a constructor takes: <see cref="Via"/> is null for the class's own constructor.</summary>
        internal sealed record RequestStateEdge(string? Via, string State, EquatableArray<string> Names);

        // The classes of a project that implement each interface, found once per compilation when a class takes an interface
        private static readonly ConditionalWeakTable<Compilation, ILookup<ISymbol, INamedTypeSymbol>> ImplementationsByCompilation = new();

        private static RequestStateClass? RequestStateClassOf(SemanticModel model, TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
        {
            if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type) return null;
            var interfaces = type.AllInterfaces
                .Where(i => !IsFrameworkType(i))
                .Select(i => EntityMappingMetadata.MetadataName(i.OriginalDefinition))
                .Where(name => name != null)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (interfaces.Count == 0) return null;

            var edges = new List<RequestStateEdge>();
            var taken = RequestState.Constructors(type).SelectMany(c => c.Parameters).Select(p => p.Type).ToList();
            foreach (var parameter in taken) edges.Add(Edge(null, parameter));

            var compilation = model.Compilation;
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { type };
            foreach (var parameter in taken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A framework class, such as HttpClient, is no class of the application's that reads the request
                if (IsFrameworkType(parameter) && parameter.TypeKind == TypeKind.Class) continue;
                var implementations = ImplementationsByCompilation.GetValue(compilation, RequestState.Implementations);
                foreach (var dependency in RequestState.Classes(parameter, implementations))
                {
                    if (IsFrameworkType(dependency) || !seen.Add(dependency)) continue;
                    var via = RequestState.Display(dependency);
                    foreach (var inner in RequestState.Constructors(dependency).SelectMany(c => c.Parameters)) edges.Add(Edge(via, inner.Type));
                }
            }
            if (edges.Count == 0) return null;
            return new RequestStateClass(declaration.SyntaxTree, new EquatableArray<string>(interfaces!), RequestState.Display(type), new EquatableArray<RequestStateEdge>(edges));

            static RequestStateEdge Edge(string? via, ITypeSymbol state)
                => new(via, RequestState.Display(state), new EquatableArray<string>(RequestState.Names(state).Distinct(StringComparer.Ordinal)));
        }

        private static EquatableArray<string> RequestStateValues(RequestStateClass candidate, AnalyzerConfigOptionsProvider options)
        {
            var configured = options.GetOptions(candidate.Tree).TryGetValue(RequestState.Option, out var value)
                ? RequestState.Configured(value)
                : RequestState.Configured(null);
            var edge = candidate.Edges.FirstOrDefault(e => e.Names.Any(name => RequestState.BuiltIn.Contains(name) || configured.Contains(name)));
            return edge == null
                ? EquatableArray<string>.Empty
                : new EquatableArray<string>(candidate.Interfaces.Select(i => RequestState.Write(i, candidate.Class, edge.Via, edge.State)));
        }

        private static void EmitRequestState(SourceProductionContext context, EquatableArray<string> values)
        {
            if (values.Count == 0) return;

            var source = new StringBuilder()
                .AppendLine("// <auto-generated/>")
                .AppendLine("// The classes of this project whose constructors take per-request state, by the interfaces they implement. MC0026")
                .AppendLine("// reads them in the projects that reference this one, where a cached implementation takes one of the interfaces.");
            foreach (var value in values)
            {
                source.Append("[assembly: global::System.Reflection.AssemblyMetadata(")
                    .Append(SymbolDisplay.FormatLiteral(RequestState.MetadataKey, quote: true))
                    .Append(", ")
                    .Append(SymbolDisplay.FormatLiteral(value, quote: true))
                    .AppendLine(")]");
            }
            context.AddSource("MethodCacheRequestState.g.cs", source.ToString());
        }
    }
}
