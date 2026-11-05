using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MethodCache.Internal;

namespace MethodCache.CodeFixes
{
    /// <summary>
    /// MC0004: a duration written as a TimeSpan, such as <c>Duration = "00:10:00"</c>. Writes the same value with a unit,
    /// <c>"10m"</c>.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DurationUnitCodeFixProvider)), Shared]
    public sealed class DurationUnitCodeFixProvider : CodeFixProvider
    {
        private static readonly ImmutableHashSet<string> Durations = ImmutableHashSet.Create(
            "Duration", "SlidingExpiration", "RefreshAhead", "FailSafe", "FailSafeTimeout", "FailSafeRetryAfter");

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("MC0004");

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        /// <inheritdoc />
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) return;

            foreach (var diagnostic in context.Diagnostics)
            {
                // The diagnostic covers the attribute; each of its durations written as a TimeSpan gets a fix
                var attribute = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<AttributeSyntax>();
                if (attribute?.ArgumentList is null) continue;
                foreach (var argument in attribute.ArgumentList.Arguments)
                {
                    if (argument.NameEquals is not { } name || !Durations.Contains(name.Name.Identifier.ValueText)
                        || argument.Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression)
                        || DurationParser.Suggest(literal.Token.ValueText) is not { } suggestion)
                    {
                        continue;
                    }

                    context.RegisterCodeFix(
                        CodeAction.Create(
                            $"Write {name.Name.Identifier.ValueText} as \"{suggestion}\"",
                            _ =>
                            {
                                var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(suggestion))
                                    .WithTriviaFrom(literal);
                                return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(literal, replacement)));
                            },
                            equivalenceKey: "WriteDurationWithUnit:" + name.Name.Identifier.ValueText),
                        diagnostic);
                }
            }
        }
    }
}
