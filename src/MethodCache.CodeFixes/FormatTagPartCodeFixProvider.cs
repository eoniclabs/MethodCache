using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.CodeFixes
{
    /// <summary>
    /// MC0024: a hand-built tag such as <c>$"plan:{tenantId}"</c> writes a value differently from MethodCache. Wraps the
    /// value in <c>MethodCacheKeys.Part</c>, which formats it the way MethodCache's own tags do.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FormatTagPartCodeFixProvider)), Shared]
    public sealed class FormatTagPartCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("MC0024");

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        /// <inheritdoc />
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || model is null) return;

            foreach (var diagnostic in context.Diagnostics)
            {
                if (!diagnostic.Properties.ContainsKey("Fixable")) continue;
                if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<InterpolationSyntax>() is not { } hole)
                {
                    continue;
                }

                // The short name when MethodCacheKeys already resolves there, otherwise the full one
                var shortName = model.LookupNamespacesAndTypes(hole.SpanStart, name: "MethodCacheKeys");
                var typeName = shortName.Length == 1 && shortName[0].ContainingNamespace?.Name == "MethodCache"
                    ? "MethodCacheKeys"
                    : "global::MethodCache.MethodCacheKeys";

                context.RegisterCodeFix(
                    CodeAction.Create(
                        $"Format with {typeName.Replace("global::", string.Empty)}.Part",
                        _ =>
                        {
                            // Inside an interpolation hole, "::" would start a format clause: keep a global:: call in parentheses
                            var text = $"{typeName}.Part({hole.Expression.WithoutTrivia()})";
                            var call = SyntaxFactory.ParseExpression(typeName.StartsWith("global::", StringComparison.Ordinal) ? $"({text})" : text)
                                .WithTriviaFrom(hole.Expression);
                            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(hole.Expression, call)));
                        },
                        equivalenceKey: "FormatWithMethodCacheKeysPart"),
                    diagnostic);
            }
        }
    }
}
