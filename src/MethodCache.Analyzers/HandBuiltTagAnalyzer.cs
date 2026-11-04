using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// MC0024: an interpolated tag passed to <c>InvalidateTagAsync</c> or <c>InvalidateTagsAsync</c> formats an argument
    /// differently from the tags MethodCache builds, so the invalidation can silently match nothing.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HandBuiltTagAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>The id of the diagnostic for a hand-built tag argument that MethodCache formats differently (MC0024).</summary>
        public const string DiagnosticId = "MC0024";

        /// <summary>Set on a diagnostic whose value <c>MethodCacheKeys.Part</c> can format, so the code fix can wrap it.</summary>
        public const string FixableProperty = "Fixable";

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            "A hand-built tag formats an argument differently from MethodCache",
            "'{0}' is written into the tag as {1}, but MethodCache writes it differently in the tags it stores, so this invalidation can silently match nothing. Use IMethodCacheKeys.Tag, or format it with MethodCacheKeys.Part.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Tag templates such as \"user:{id}\" format arguments in a canonical way: a Guid as 32 digits without hyphens, an enum as its number, true and false in lower case, dates in ISO 8601, numbers in the invariant culture, and ':' and '\\' escaped in strings. String interpolation formats them differently, so a tag built by hand for InvalidateTagAsync doesn't match the cached entries. IMethodCacheKeys.Tag builds the tag from a call to the cached method; MethodCacheKeys.Part formats one value.",
            helpLinkUri: "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#mc0024");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => null
            };
            if (name is not ("InvalidateTagAsync" or "InvalidateTagsAsync")) return;
            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
                !IsMethodCacheInvalidation(method))
            {
                return;
            }

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                foreach (var text in InterpolatedStrings(argument.Expression))
                {
                    foreach (var content in text.Contents)
                    {
                        if (content is InterpolationSyntax hole) Check(context, hole);
                    }
                }
            }
        }

        private static bool IsMethodCacheInvalidation(IMethodSymbol method)
        {
            var type = method.ReducedFrom?.ContainingType ?? method.ContainingType;
            if (type is null) return false;
            if (IsMethodCacheType(type, "ICacheManager") || IsMethodCacheType(type, "ICacheAdministrator") ||
                IsMethodCacheType(type, "CacheManagerExtensions"))
            {
                return true;
            }

            foreach (var implemented in type.AllInterfaces)
            {
                if (IsMethodCacheType(implemented, "ICacheManager") || IsMethodCacheType(implemented, "ICacheAdministrator")) return true;
            }

            return false;
        }

        private static bool IsMethodCacheType(INamedTypeSymbol type, string name)
            => type.Name == name && type.ContainingNamespace is { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true };

        // The tag itself, or the elements of the array or collection expression that holds the tags
        private static IEnumerable<InterpolatedStringExpressionSyntax> InterpolatedStrings(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case InterpolatedStringExpressionSyntax text:
                    yield return text;
                    break;
                case CollectionExpressionSyntax collection:
                    foreach (var element in collection.Elements)
                    {
                        if (element is ExpressionElementSyntax { Expression: InterpolatedStringExpressionSyntax text }) yield return text;
                    }
                    break;
                case ArrayCreationExpressionSyntax { Initializer: { } initializer }:
                    foreach (var text in Initialized(initializer)) yield return text;
                    break;
                case ImplicitArrayCreationExpressionSyntax { Initializer: { } initializer }:
                    foreach (var text in Initialized(initializer)) yield return text;
                    break;
            }
        }

        private static IEnumerable<InterpolatedStringExpressionSyntax> Initialized(InitializerExpressionSyntax initializer)
        {
            foreach (var element in initializer.Expressions)
            {
                if (element is InterpolatedStringExpressionSyntax text) yield return text;
            }
        }

        private static void Check(SyntaxNodeAnalysisContext context, InterpolationSyntax hole)
        {
            var model = context.SemanticModel;
            var expression = hole.Expression;
            if (IsPartCall(model, expression, context)) return;

            var constant = model.GetConstantValue(expression, context.CancellationToken);
            if (constant.HasValue && constant.Value is string literal && literal.IndexOf(':') < 0 && literal.IndexOf('\\') < 0) return;

            var type = model.GetTypeInfo(expression, context.CancellationToken).Type;
            if (type is null) return;
            var formatted = hole.FormatClause is not null || hole.AlignmentClause is not null;

            // Whole numbers come out the same, unless a format or alignment changes them
            if (!formatted && IsWholeNumber(type)) return;

            var properties = ImmutableDictionary<string, string?>.Empty;
            if (!formatted && PartHasOverloadFor(type)) properties = properties.Add(FixableProperty, "true");

            context.ReportDiagnostic(Diagnostic.Create(Rule, hole.GetLocation(), properties, expression.ToString(), Describe(type, formatted)));
        }

        private static bool IsPartCall(SemanticModel model, ExpressionSyntax expression, SyntaxNodeAnalysisContext context)
            => expression is InvocationExpressionSyntax &&
               model.GetSymbolInfo(expression, context.CancellationToken).Symbol is IMethodSymbol { Name: "Part" } part &&
               IsMethodCacheType(part.ContainingType, "MethodCacheKeys");

        private static bool IsWholeNumber(ITypeSymbol type) => type.SpecialType is
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

        // The types MethodCacheKeys.Part takes directly; a nullable value type or another type needs IMethodCacheKeys.Tag
        private static bool PartHasOverloadFor(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum) return true;
            if (type.SpecialType is SpecialType.System_String or SpecialType.System_Char or SpecialType.System_Boolean or
                SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_DateTime)
            {
                return true;
            }

            return type is INamedTypeSymbol { ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } named &&
                   named.Name is "Guid" or "DateTimeOffset";
        }

        private static string Describe(ITypeSymbol type, bool formatted)
        {
            if (formatted) return "the format you chose";
            if (type.TypeKind == TypeKind.Enum) return "the member's name (MethodCache writes its number)";
            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                case SpecialType.System_Char:
                    return "is (MethodCache escapes ':' and '\\')";
                case SpecialType.System_Boolean:
                    return "True or False (MethodCache writes true or false)";
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_DateTime:
                    return "text in the current culture (MethodCache uses the invariant culture)";
            }

            if (type.Name == "Guid") return "a Guid with hyphens (MethodCache writes 32 digits without hyphens)";
            if (type.Name == "DateTimeOffset") return "text in the current culture (MethodCache uses ISO 8601)";
            return "its ToString() text";
        }
    }
}
