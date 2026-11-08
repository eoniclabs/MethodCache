using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace MethodCache.CodeFixes
{
    /// <summary>
    /// MC0040: adds <c>[JsonSerializable(typeof(Dog))]</c> to the <c>JsonSerializerContext</c> passed to <c>UseJson</c>,
    /// so the distributed cache can restore the derived type as itself.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddJsonSerializableCodeFixProvider)), Shared]
    public sealed class AddJsonSerializableCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("MC0040");

        /// <inheritdoc />
        public override FixAllProvider? GetFixAllProvider() => null;

        /// <inheritdoc />
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                return;
            }

            foreach (var diagnostic in context.Diagnostics)
            {
                if (!diagnostic.Properties.TryGetValue("Context", out var contextId) || contextId is null ||
                    !diagnostic.Properties.TryGetValue("DerivedType", out var derivedType) || derivedType is null ||
                    DocumentationCommentId.GetFirstSymbolForDeclarationId(contextId, model.Compilation) is not INamedTypeSymbol jsonContext ||
                    Declaration(jsonContext, context.CancellationToken) is not { } declaration)
                {
                    continue;
                }

                var name = derivedType.StartsWith("global::", System.StringComparison.Ordinal) ? derivedType.Substring("global::".Length) : derivedType;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        $"Add [JsonSerializable(typeof({name.Split('.').Last()}))] to {jsonContext.Name}",
                        ct => AddAsync(context.Document.Project.Solution, declaration, derivedType, ct),
                        equivalenceKey: "AddJsonSerializable:" + derivedType),
                    diagnostic);
            }
        }

        /// <summary>The part of the context that lists its types, rather than the one System.Text.Json's generator adds.</summary>
        private static SyntaxReference? Declaration(INamedTypeSymbol jsonContext, CancellationToken cancellationToken)
            => jsonContext.DeclaringSyntaxReferences.FirstOrDefault(reference => reference.GetSyntax(cancellationToken) is ClassDeclarationSyntax declaration &&
                    declaration.AttributeLists.SelectMany(static list => list.Attributes)
                        .Any(static attribute => attribute.Name.ToString().Split('.').Last() is "JsonSerializable" or "JsonSerializableAttribute"))
               ?? jsonContext.DeclaringSyntaxReferences.FirstOrDefault();

        // The whitespace before the last attribute list on its line, without the blank lines or comments above it
        private static SyntaxTriviaList Indentation(SyntaxNode node)
            => SyntaxFactory.TriviaList(node.GetLeadingTrivia().Reverse().TakeWhile(static t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse());

        private static async Task<Solution> AddAsync(Solution solution, SyntaxReference declaration, string derivedType, CancellationToken cancellationToken)
        {
            var document = solution.GetDocument(declaration.SyntaxTree);
            if (document is null || await declaration.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) is not ClassDeclarationSyntax jsonContext)
            {
                return solution;
            }

            var attribute = SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("global::System.Text.Json.Serialization.JsonSerializable"),
                    SyntaxFactory.ParseAttributeArgumentList($"(typeof({derivedType}))"))
                .WithAdditionalAnnotations(Simplifier.Annotation);
            var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute)).WithAdditionalAnnotations(Formatter.Annotation);

            // After the context's last [JsonSerializable], or before the class when it has none
            var lists = jsonContext.AttributeLists;
            var updated = lists.Count == 0
                ? jsonContext.WithAttributeLists(lists.Add(list.WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)))
                    .WithLeadingTrivia(jsonContext.GetLeadingTrivia())
                : jsonContext.WithAttributeLists(lists.Insert(lists.Count, list.WithLeadingTrivia(Indentation(lists.Last()))));

            var root = await declaration.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(jsonContext, updated));
        }
    }
}
