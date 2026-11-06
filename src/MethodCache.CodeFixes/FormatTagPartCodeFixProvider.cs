using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
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
    /// tag in <c>MethodCacheKeys.Tag</c> when every value in it can go there, or the value in <c>MethodCacheKeys.Part</c>,
    /// which format values the way MethodCache's own tags do.
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

                // The whole tag in MethodCacheKeys.Tag, when every value in it is one the tag handler writes
                if (hole.Parent is InterpolatedStringExpressionSyntax text && text.Contents.OfType<InterpolationSyntax>().All(h => Writable(model, h)))
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            $"Wrap the tag in {typeName.Replace("global::", string.Empty)}.Tag",
                            _ =>
                            {
                                // A value already formatted with Part would be escaped twice, so it goes in as it is
                                var unwrapped = text.ReplaceNodes(
                                    text.Contents.OfType<InterpolationSyntax>().Where(h => PartArgument(model, h.Expression) != null).Select(h => h.Expression),
                                    (original, _) => PartArgument(model, original)!.WithTriviaFrom(original));
                                var call = SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.ParseExpression(typeName + ".Tag"),
                                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(unwrapped.WithoutTrivia()))))
                                    .WithTriviaFrom(text);
                                return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(text, call)));
                            },
                            equivalenceKey: "WrapInMethodCacheKeysTag"),
                        diagnostic);
                }

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

        // A value without a format or alignment of a type MethodCacheKeys.Tag's handler takes, or a call to Part
        private static bool Writable(SemanticModel model, InterpolationSyntax hole)
        {
            if (hole.FormatClause is not null || hole.AlignmentClause is not null) return false;
            if (PartArgument(model, hole.Expression) != null) return true;
            var type = model.GetTypeInfo(hole.Expression).Type;
            if (type is null) return false;
            if (type.TypeKind == TypeKind.Enum) return true;
            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                case SpecialType.System_Char:
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_DateTime:
                    return true;
            }

            return type is INamedTypeSymbol { ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } named &&
                   named.Name is "Guid" or "DateTimeOffset";
        }

        // x in a hole MethodCacheKeys.Part(x), in parentheses or not
        private static ExpressionSyntax? PartArgument(SemanticModel model, ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            return expression is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } invocation &&
                   model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "Part", ContainingType: { Name: "MethodCacheKeys", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } } }
                ? invocation.ArgumentList.Arguments[0].Expression
                : null;
        }
    }
}
