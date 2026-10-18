using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.CodeFixes
{
    /// <summary>
    /// Removes [Cache] from a method MethodCache can't cache (void, plain Task, ref/out/in, pointer, ref struct or
    /// lazy sequence). The diagnostic says how to change the method instead; this is the fix when caching it was a mistake.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveCacheAttributeCodeFixProvider)), Shared]
    public sealed class RemoveCacheAttributeCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create("MC0003", "MC0022", "MC0005", "MC0008", "MC0009", "MC0015");

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

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
                var method = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();
                var attribute = method?.AttributeLists
                    .SelectMany(l => l.Attributes)
                    .FirstOrDefault(a => MethodCacheAttributes.GetName(a, model, context.CancellationToken) == MethodCacheAttributes.Cache);
                if (attribute is null)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Remove [Cache]",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(MethodCacheAttributes.RemoveAttribute(root, attribute))),
                        equivalenceKey: "RemoveCache"),
                    diagnostic);
            }
        }
    }
}
