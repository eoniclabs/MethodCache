using System.Collections.Generic;
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
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.CodeFixes
{
    /// <summary>
    /// MC0035: a cache factory uses a token from outside it that the call doesn't pass. Makes the factory use its own
    /// token parameter, naming it after the outer token when it is a discard, and passes the outer token as the call's
    /// <c>cancellationToken</c>, so cancelling it stops this caller's wait and the others load again.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FactoryTokenCodeFixProvider)), Shared]
    public sealed class FactoryTokenCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("MC0035");

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
                if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not ExpressionSyntax used
                    || Factory(used) is not { } factory
                    || factory.Parent?.Parent?.Parent is not InvocationExpressionSyntax invocation
                    || model.GetSymbolInfo(factory, context.CancellationToken).Symbol is not IMethodSymbol lambda
                    || lambda.Parameters.FirstOrDefault(p => p.Type.Name == "CancellationToken") is not { } own)
                {
                    continue;
                }

                var outer = used.WithoutTrivia().ToString();
                var passes = model.GetOperation(invocation, context.CancellationToken) is IInvocationOperation call
                    && call.Arguments.Any(a => a.Parameter?.Name == "cancellationToken" && a.ArgumentKind == ArgumentKind.Explicit);
                var title = passes ? "Use the factory's own token" : $"Use the factory's own token and pass '{outer}' as cancellationToken";
                context.RegisterCodeFix(
                    CodeAction.Create(title, token => FixAsync(context.Document, root, model, invocation, factory, own, used, outer, passes, token), equivalenceKey: "UseFactoryToken"),
                    diagnostic);
            }
        }

        // The lambda passed to the cache call that the token is used in
        private static AnonymousFunctionExpressionSyntax? Factory(SyntaxNode used)
            => used.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault(f =>
                f.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax { Expression: var name } }
                && (name is MemberAccessExpressionSyntax { Name.Identifier.Text: "GetOrCreateAsync" or "GetOrCreateManyAsync" }
                    || name is SimpleNameSyntax { Identifier.Text: "GetOrCreateAsync" or "GetOrCreateManyAsync" }));

        private static Task<Document> FixAsync(Document document, SyntaxNode root, SemanticModel model, InvocationExpressionSyntax invocation,
            AnonymousFunctionExpressionSyntax factory, IParameterSymbol own, ExpressionSyntax used, string outer, bool passes, CancellationToken cancellationToken)
        {
            var parameter = factory switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter,
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters[own.Ordinal],
                AnonymousMethodExpressionSyntax { ParameterList: { } list } method => list.Parameters[own.Ordinal],
                _ => null,
            };
            if (parameter == null) return Task.FromResult(document);

            // A discard gets the outer token's name, which shadows it inside the factory, or ct when that is an expression
            var name = own.Name;
            if (own.Name.Trim('_').Length == 0)
            {
                var taken = new HashSet<string>(factory.DescendantNodes().OfType<ParameterSyntax>().Select(p => p.Identifier.Text)
                    .Concat(factory.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.Text))
                    .Concat(factory.DescendantNodes().OfType<SingleVariableDesignationSyntax>().Select(v => v.Identifier.Text)));
                name = used is IdentifierNameSyntax identifier && !taken.Contains(identifier.Identifier.Text) ? identifier.Identifier.Text
                    : !taken.Contains("ct") ? "ct" : "token";
            }

            // Every use of the outer token in the factory, as the diagnostic names it
            var uses = factory.DescendantNodes().OfType<ExpressionSyntax>().Where(e =>
                e.WithoutTrivia().ToString() == outer
                && !(e.Parent is MemberAccessExpressionSyntax access && access.Name == e)
                && model.GetTypeInfo(e, cancellationToken).Type?.Name == "CancellationToken"
                && !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(e, cancellationToken).Symbol, own)).ToList();

            var replaced = invocation.ReplaceNodes(
                uses.Cast<SyntaxNode>().Append(parameter),
                (original, rewritten) => original == parameter
                    ? ((ParameterSyntax)rewritten).WithIdentifier(SyntaxFactory.Identifier(name).WithTriviaFrom(parameter.Identifier))
                    : SyntaxFactory.IdentifierName(name).WithTriviaFrom(original));
            if (!passes)
            {
                replaced = replaced.AddArgumentListArguments(SyntaxFactory.Argument(
                    SyntaxFactory.NameColon("cancellationToken"), default, SyntaxFactory.ParseExpression(outer)));
            }

            return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(invocation, replaced)));
        }
    }
}
