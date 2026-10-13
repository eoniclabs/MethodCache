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
    /// MC0001: moves [Cache] or [CacheInvalidate] from a class member to the interface method it implements,
    /// or removes it when the member implements no interface method in this solution.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MoveAttributeToInterfaceCodeFixProvider)), Shared]
    public sealed class MoveAttributeToInterfaceCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("MC0001");

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
                var attribute = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<AttributeSyntax>();
                var method = attribute?.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                var attributeName = attribute is null ? null : MethodCacheAttributes.GetName(attribute, model, context.CancellationToken);
                if (attribute is null || method is null || attributeName is null)
                {
                    continue;
                }

                var display = MethodCacheAttributes.DisplayName(attributeName);
                var target = model.GetDeclaredSymbol(method, context.CancellationToken) is IMethodSymbol symbol
                    ? FindInterfaceMethod(symbol, context.CancellationToken)
                    : null;

                if (target is not null)
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            $"Move [{display}] to {target.Value.Symbol.ContainingType.Name}.{target.Value.Symbol.Name}",
                            ct => MoveAsync(context.Document, attribute, attributeName, target.Value.Reference, ct),
                            equivalenceKey: "MoveToInterface"),
                        diagnostic);
                }
                else
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            $"Remove [{display}]",
                            ct => Task.FromResult(context.Document.WithSyntaxRoot(MethodCacheAttributes.RemoveAttribute(root, attribute))),
                            equivalenceKey: "RemoveAttribute"),
                        diagnostic);
                }
            }
        }

        private static (IMethodSymbol Symbol, SyntaxReference Reference)? FindInterfaceMethod(IMethodSymbol method, CancellationToken cancellationToken)
        {
            var candidates = method.ExplicitInterfaceImplementations.AsEnumerable();
            if (method.ContainingType is { } type)
            {
                candidates = candidates.Concat(
                    type.AllInterfaces
                        .SelectMany(i => i.GetMembers(method.Name).OfType<IMethodSymbol>())
                        .Where(m => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), method)));
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = candidate.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault();
                if (reference is not null)
                {
                    return (candidate.OriginalDefinition, reference);
                }
            }

            return null;
        }

        private static async Task<Solution> MoveAsync(Document document, AttributeSyntax attribute, string attributeName, SyntaxReference target, CancellationToken cancellationToken)
        {
            var solution = document.Project.Solution;
            var targetDocument = solution.GetDocument(target.SyntaxTree);
            if (targetDocument is null || await target.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) is not MethodDeclarationSyntax targetMethod)
            {
                return solution;
            }

            // Leave the interface alone when it already carries the same attribute: the one on the class is just noise
            var targetModel = await targetDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var alreadyThere = targetModel is not null && targetMethod.AttributeLists
                .SelectMany(l => l.Attributes)
                .Any(a => MethodCacheAttributes.GetName(a, targetModel, cancellationToken) == attributeName);

            var sourceRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (sourceRoot is null)
            {
                return solution;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var attributeNamespace = model?.GetTypeInfo(attribute, cancellationToken).Type?.ContainingNamespace?.ToDisplayString();
            var movedAttribute = model is null ? attribute : (AttributeSyntax)new PortableAttributeRewriter(model, cancellationToken).Visit(attribute)!;
            if (model?.GetTypeInfo(attribute, cancellationToken).Type is { } attributeType)
            {
                movedAttribute = movedAttribute.WithName(SyntaxFactory.ParseName(attributeType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .WithAdditionalAnnotations(Simplifier.Annotation));
            }

            if (targetDocument.Id == document.Id)
            {
                // Same file: edit both methods in one tree
                var tracked = sourceRoot.TrackNodes(attribute, targetMethod);
                tracked = MethodCacheAttributes.RemoveAttribute(tracked, tracked.GetCurrentNode(attribute)!);
                if (!alreadyThere)
                {
                    var current = tracked.GetCurrentNode(targetMethod)!;
                    tracked = tracked.ReplaceNode(current, AddAttribute(current, movedAttribute));
                }

                return solution.WithDocumentSyntaxRoot(document.Id, tracked);
            }

            solution = solution.WithDocumentSyntaxRoot(document.Id, MethodCacheAttributes.RemoveAttribute(sourceRoot, attribute));
            if (alreadyThere)
            {
                return solution;
            }

            var targetRoot = await target.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var newTargetRoot = targetRoot.ReplaceNode(targetMethod, AddAttribute(targetMethod, movedAttribute));
            if (attributeNamespace is not null && newTargetRoot is CompilationUnitSyntax unit && !Resolves(targetModel, targetMethod.SpanStart, attributeName, attributeNamespace))
            {
                newTargetRoot = unit.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(attributeNamespace)).WithAdditionalAnnotations(Formatter.Annotation));
            }

            return solution.WithDocumentSyntaxRoot(targetDocument.Id, newTargetRoot);
        }

        private sealed class PortableAttributeRewriter : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _model;
            private readonly CancellationToken _cancellationToken;

            public PortableAttributeRewriter(SemanticModel model, CancellationToken cancellationToken)
            {
                _model = model;
                _cancellationToken = cancellationToken;
            }

            public override SyntaxNode? Visit(SyntaxNode? node)
            {
                if (node is ExpressionSyntax expression)
                {
                    var constant = _model.GetConstantValue(expression, _cancellationToken);
                    if (constant.HasValue)
                    {
                        var literal = constant.Value is null
                            ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                            : SyntaxFactory.ParseExpression(SymbolDisplay.FormatPrimitive(constant.Value, true, false));
                        return literal.WithTriviaFrom(expression);
                    }

                    if (expression is TypeSyntax && _model.GetSymbolInfo(expression, _cancellationToken).Symbol is ITypeSymbol type)
                    {
                        return SyntaxFactory.ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                            .WithTriviaFrom(expression).WithAdditionalAnnotations(Simplifier.Annotation);
                    }
                }
                return base.Visit(node);
            }
        }

        private static MethodDeclarationSyntax AddAttribute(MethodDeclarationSyntax method, AttributeSyntax attribute)
        {
            var leading = method.GetLeadingTrivia();
            var indentation = leading.Reverse().TakeWhile(t => !t.IsKind(SyntaxKind.EndOfLineTrivia)).Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
            var endOfLine = method.SyntaxTree.GetRoot().DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            if (endOfLine == default)
            {
                endOfLine = SyntaxFactory.EndOfLine("\n");
            }

            var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute.WithoutTrivia()));
            if (method.AttributeLists.Count > 0)
            {
                // After the existing attributes, on its own line
                return method.AddAttributeLists(list.WithLeadingTrivia(indentation).WithTrailingTrivia(endOfLine));
            }

            // On its own line above the method; the method keeps its comments and indentation
            return method
                .WithLeadingTrivia(indentation)
                .AddAttributeLists(list.WithLeadingTrivia(leading).WithTrailingTrivia(endOfLine));
        }

        // True when the attribute type already resolves where the interface method is declared (a using, a global
        // using, or the same namespace), so no using needs to be added
        private static bool Resolves(SemanticModel? model, int position, string attributeName, string ns)
        {
            var type = model?.GetSpeculativeTypeInfo(position, SyntaxFactory.ParseName(attributeName), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            return type is not null && type.TypeKind != TypeKind.Error && type.ContainingNamespace?.ToDisplayString() == ns;
        }
    }
}
