using System;
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

namespace MethodCache.CodeFixes
{
    /// <summary>
    /// MCG009: a tag such as "user:{usrId}" names a parameter the method doesn't have. Offers the closest
    /// parameter names, so a typo becomes "user:{userId}".
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FixTagParameterCodeFixProvider)), Shared]
    public sealed class FixTagParameterCodeFixProvider : CodeFixProvider
    {
        private const int MaxSuggestions = 3;

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("MCG009");

        /// <inheritdoc />
        public override FixAllProvider? GetFixAllProvider() => null;

        /// <inheritdoc />
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
            {
                return;
            }

            foreach (var diagnostic in context.Diagnostics)
            {
                if (!diagnostic.Properties.TryGetValue("Parameter", out var unknown) || string.IsNullOrEmpty(unknown))
                {
                    continue;
                }

                var method = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (method is null)
                {
                    continue;
                }

                var placeholder = "{" + unknown + "}";
                var literals = TagLiterals(method, model, context.CancellationToken)
                    .Where(l => l.Token.ValueText.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToImmutableArray();
                if (literals.IsEmpty)
                {
                    continue;
                }

                var suggestions = method.ParameterList.Parameters
                    .Select(p => p.Identifier.ValueText)
                    .Where(n => n.Length > 0)
                    .OrderBy(n => Distance(n, unknown!))
                    .ThenBy(n => n, StringComparer.Ordinal)
                    .Take(MaxSuggestions);

                foreach (var name in suggestions)
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            $"Use parameter '{name}' in the tag",
                            _ => Task.FromResult(context.Document.WithSyntaxRoot(Replace(root, literals, placeholder, "{" + name + "}"))),
                            equivalenceKey: "UseParameter:" + name),
                        diagnostic);
                }
            }
        }

        private static ImmutableArray<LiteralExpressionSyntax> TagLiterals(MethodDeclarationSyntax method, SemanticModel model, CancellationToken cancellationToken)
            => method.AttributeLists
                .SelectMany(l => l.Attributes)
                .Where(a => MethodCacheAttributes.GetName(a, model, cancellationToken) is not null)
                .SelectMany(a => a.ArgumentList?.Arguments ?? default)
                .Where(MethodCacheAttributes.IsTagsArgument)
                .SelectMany(a => a.Expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
                .Where(MethodCacheAttributes.IsStringLiteral)
                .ToImmutableArray();

        private static SyntaxNode Replace(SyntaxNode root, ImmutableArray<LiteralExpressionSyntax> literals, string placeholder, string replacement)
            => root.ReplaceNodes(literals, (original, _) =>
            {
                var text = ReplaceIgnoreCase(original.Token.ValueText, placeholder, replacement);
                return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(text)).WithTriviaFrom(original);
            });

        private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
        {
            var index = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                text = text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
                index = text.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
            }

            return text;
        }

        // Case-insensitive Levenshtein distance, enough to rank a handful of parameter names
        private static int Distance(string a, string b)
        {
            a = a.ToUpperInvariant();
            b = b.ToUpperInvariant();
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
