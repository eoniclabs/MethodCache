using System;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.CodeFixes
{
    internal static class MethodCacheAttributes
    {
        public const string Cache = "CacheAttribute";
        public const string CacheInvalidate = "CacheInvalidateAttribute";

        /// <summary>The attribute class name ("CacheAttribute") when the attribute is one of MethodCache's, else null.</summary>
        public static string? GetName(AttributeSyntax attribute, SemanticModel model, CancellationToken cancellationToken)
        {
            var type = model.GetTypeInfo(attribute, cancellationToken).Type ?? (model.GetSymbolInfo(attribute, cancellationToken).Symbol as IMethodSymbol)?.ContainingType;
            if (type is null || (type.Name != Cache && type.Name != CacheInvalidate))
            {
                return null;
            }

            var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns == "MethodCache" || ns.StartsWith("MethodCache.", StringComparison.Ordinal) ? type.Name : null;
        }

        /// <summary>Removes the attribute, and its brackets (with their line) when it was the only attribute in them.</summary>
        public static SyntaxNode RemoveAttribute(SyntaxNode root, AttributeSyntax attribute)
        {
            if (attribute.Parent is not AttributeListSyntax list || list.Attributes.Count > 1)
            {
                return root.RemoveNode(attribute, SyntaxRemoveOptions.KeepNoTrivia)!;
            }

            if (list.Parent is not MemberDeclarationSyntax member)
            {
                return root.RemoveNode(list, SyntaxRemoveOptions.KeepNoTrivia)!;
            }

            var updated = member.WithAttributeLists(member.AttributeLists.Remove(list));
            if (member.AttributeLists.IndexOf(list) == 0)
            {
                // The first list carried the member's comments and indentation; the next token inherits them
                updated = updated.WithLeadingTrivia(list.GetLeadingTrivia());
            }

            return root.ReplaceNode(member, updated);
        }

        public static string DisplayName(string attributeName) => attributeName.Substring(0, attributeName.Length - "Attribute".Length);

        public static bool IsTagsArgument(AttributeArgumentSyntax argument)
            => argument.NameEquals?.Name.Identifier.ValueText == "Tags"
               || argument.NameColon?.Name.Identifier.ValueText == "tags";

        public static bool IsStringLiteral(LiteralExpressionSyntax literal) => literal.IsKind(SyntaxKind.StringLiteralExpression);
    }
}
