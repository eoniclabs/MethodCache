using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Internal
{
    // Compiled into the analyzer and the generator. MC0028 reads the tags a project invalidates to find tags nothing
    // invalidates, and the generator writes them into the project's assembly, where TagCoverage in MethodCache.Testing
    // reads them for the whole application.
    internal static class InvalidationTags
    {
        /// <summary>
        /// <c>[assembly: AssemblyMetadata(MetadataKey, "product:{}")]</c>, once per tag the project invalidates, with
        /// <c>{}</c> for a placeholder or an interpolated value.
        /// </summary>
        internal const string MetadataKey = "MethodCache.InvalidatedTags";

        /// <summary>Stands for a placeholder or an interpolated value: any text.</summary>
        private const char Any = TagPattern.Any;

        /// <summary>
        /// The tag patterns an invocation invalidates, as <see cref="TagPattern"/> writes them, or null when it is no
        /// invalidation. A MethodCache invalidation whose tags can't be read gives an empty list: it still invalidates.
        /// </summary>
        public static IReadOnlyList<string>? Of(IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            if (method.Name is "InvalidateTagAsync" or "InvalidateTagsAsync" or "InvalidateAfterCommit")
                return IsMethodCacheInvalidation(method) ? Tags(invocation, template: false) : null;
            if (IsEntityMapping(method)) return Tags(invocation, template: true);

            // A call to a [CacheInvalidate] method invalidates its tags, also one of an interface in another project
            if (method.ContainingType?.TypeKind == TypeKind.Interface && CacheInvalidateTags(method) is { } declared) return declared;

            // A call to a helper of the app's own that invalidates the tags it is given. Its attribute is looked up only
            // for calls that pass a tag that can be read.
            var given = Tags(invocation, template: false);
            return given.Count > 0 && HasInvalidatesCache(method) ? given : null;
        }

        /// <summary>The patterns of a <c>[CacheInvalidate]</c> method's tags, or null when it has none.</summary>
        public static IReadOnlyList<string>? CacheInvalidateTags(IMethodSymbol method)
        {
            var invalidate = method.GetAttributes().FirstOrDefault(a => a.AttributeClass is
                { Name: "CacheInvalidateAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } });
            var tags = invalidate?.NamedArguments.FirstOrDefault(a => a.Key == "Tags").Value;
            return tags is { Kind: TypedConstantKind.Array, IsNull: false } array
                ? array.Values.Select(v => v.Value as string).Where(v => v != null).Select(v => TagPattern.Of(v!)).ToList()
                : null;
        }

        // Test projects invalidate on purpose, to test the cache, which says nothing about the application
        private static readonly string[] TestFrameworks =
        {
            "xunit.core", "xunit.v3.core", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework", "MSTest.TestFramework", "TUnit.Core",
        };

        /// <summary>A test project: one that references a test framework.</summary>
        public static bool IsTestProject(Compilation compilation)
            => compilation.ReferencedAssemblyNames.Any(a => TestFrameworks.Contains(a.Name));

        /// <summary>A pattern as the generator writes it, with <c>{}</c> where it matches any text.</summary>
        public static string Write(string pattern) => pattern.Replace(Any.ToString(), "{}");

        /// <summary>ICacheManager, ICacheAdministrator and CacheManagerExtensions invalidations, and db.InvalidateAfterCommit(...).</summary>
        public static bool IsMethodCacheInvalidation(IMethodSymbol method)
        {
            var type = method.ReducedFrom?.ContainingType ?? method.ContainingType;
            if (type is null) return false;
            // MethodCache.EntityFrameworkCore: db.InvalidateAfterCommit(tags)
            if (method.Name == "InvalidateAfterCommit")
            {
                return type is { Name: "MethodCacheDbContextExtensions", ContainingNamespace: { Name: "EntityFrameworkCore", ContainingNamespace: { Name: "Microsoft", ContainingNamespace.IsGlobalNamespace: true } } };
            }
            if (IsMethodCacheType(type, "ICacheManager") || IsMethodCacheType(type, "ICacheAdministrator") ||
                IsMethodCacheType(type, "CacheManagerExtensions"))
            {
                return true;
            }

            return type.AllInterfaces.Any(implemented => IsMethodCacheType(implemented, "ICacheManager") || IsMethodCacheType(implemented, "ICacheAdministrator"));
        }

        /// <summary>
        /// The method, the one it overrides or an interface method it implements is marked [InvalidatesCache]: calls
        /// through the interface and through the class both count.
        /// </summary>
        public static bool HasInvalidatesCache(IMethodSymbol method)
        {
            method = method.ReducedFrom ?? method;
            for (var current = method.OriginalDefinition; current != null; current = current.OverriddenMethod?.OriginalDefinition)
            {
                if (HasMethodCacheAttribute(current, "InvalidatesCacheAttribute")) return true;
            }
            var type = method.ContainingType;
            if (method.IsStatic || type == null || type.TypeKind == TypeKind.Interface) return false;
            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers(method.Name).OfType<IMethodSymbol>())
                {
                    // Compared as definitions, so a generic method or a method of a generic type matches too
                    if (HasMethodCacheAttribute(member, "InvalidatesCacheAttribute")
                        && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method.OriginalDefinition))
                        return true;
                }
            }
            return false;
        }

        public static bool HasMethodCacheAttribute(IMethodSymbol method, string name)
            => method.GetAttributes().Any(a => a.AttributeClass is { ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } } attribute
                && attribute.Name == name);

        /// <summary>ef.Entity&lt;T&gt;("...") and the builder's Tags("...") and WhenDeleted("...").</summary>
        public static bool IsEntityMapping(IMethodSymbol method)
        {
            var type = method.ContainingType?.OriginalDefinition;
            if (type?.ContainingNamespace is not { Name: "EntityFrameworkCore", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } })
                return false;
            return (type.Name == "EntityFrameworkCoreOptions" && method.Name == "Entity")
                || (type.Name == "EntityTagsBuilder" && method.Name is "Tags" or "WhenDeleted");
        }

        /// <summary>The tags an invocation passes as constants, interpolated strings or <c>IMethodCacheKeys.Tag</c> calls.</summary>
        public static IReadOnlyList<string> Tags(IInvocationOperation invocation, bool template)
        {
            var tags = new List<string>();
            foreach (var argument in invocation.Arguments) Collect(argument.Value, template, tags);
            return tags;
        }

        private static void Collect(IOperation? value, bool template, List<string> tags)
        {
            while (value is IConversionOperation conversion) value = conversion.Operand;
            switch (value)
            {
                case null:
                    return;
                case { ConstantValue: { HasValue: true, Value: string text } }:
                    tags.Add(template ? TagPattern.Of(text) : text.Replace(Any, ' '));
                    return;
                case IInterpolatedStringOperation interpolated:
                    var pattern = new StringBuilder();
                    foreach (var part in interpolated.Parts)
                    {
                        if (LiteralText(part) is { } piece) pattern.Append(piece);
                        else pattern.Append(Any);
                    }
                    tags.Add(pattern.ToString());
                    return;
                case IInterpolatedStringHandlerCreationOperation handler:
                    // MethodCacheKeys.Tag($"..."): the interpolated string goes to a handler instead of a string
                    Collect(handler.Content, template, tags);
                    return;
                case IInvocationOperation { TargetMethod.Name: "Tag" } call when IsMethodCacheType(call.TargetMethod.ContainingType, "MethodCacheKeys"):
                    foreach (var argument in call.Arguments) Collect(argument.Value, template, tags);
                    return;
                case IInvocationOperation { TargetMethod.Name: "Tag" } call when IsKeys(call.TargetMethod.ContainingType):
                    var templateArgument = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "template")?.Value;
                    if (templateArgument?.ConstantValue is { HasValue: true, Value: string tagTemplate }) tags.Add(TagPattern.Of(tagTemplate));
                    return;
                case IArrayCreationOperation array when array.Initializer != null:
                    foreach (var element in array.Initializer.ElementValues) Collect(element, template, tags);
                    return;
                case { Syntax: CollectionExpressionSyntax }:
                    // Roslyn 4.8, which the analyzer is built against, has no operation type for a collection expression
                    foreach (var element in value.ChildOperations) Collect(element, template, tags);
                    return;
            }
        }

        // Constant text of an interpolated string: a text part, or the AppendLiteral call of a handler
        private static string? LiteralText(IInterpolatedStringContentOperation part) => part switch
        {
            IInterpolatedStringTextOperation { Text.ConstantValue: { HasValue: true, Value: string text } } => text,
            IInterpolatedStringAppendOperation { Kind: OperationKind.InterpolatedStringAppendLiteral, AppendCall: IInvocationOperation literal }
                when literal.Arguments.Length == 1 && literal.Arguments[0].Value.ConstantValue is { HasValue: true, Value: string text } => text,
            _ => null,
        };

        private static bool IsKeys(INamedTypeSymbol? type)
        {
            if (type == null) return false;
            if (IsMethodCacheType(type, "IMethodCacheKeys")) return true;
            return type.AllInterfaces.Any(i => IsMethodCacheType(i, "IMethodCacheKeys"));
        }

        private static bool IsMethodCacheType(INamedTypeSymbol? type, string name)
            => type != null && type.Name == name && type.ContainingNamespace is { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true };

        /// <summary>
        /// Whether an invocation may pass a tag, told from its syntax alone: an argument is a string literal, an
        /// interpolated string or a name that may be a constant, or an array, collection or <c>Tag(...)</c> call that
        /// holds one. Any name may be a constant in a call to a MethodCache method that takes tags; in other calls, which
        /// may be the application's own [InvalidatesCache] helpers, only a name written as constants are, such as
        /// <c>Products</c> or <c>CacheTags.Products</c>, so the generator doesn't read every call of the project.
        /// </summary>
        public static bool IsCandidate(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax invocation) return false;
            var anyName = TakesTags(invocation);
            return invocation.ArgumentList.Arguments.Any(argument => MayHoldTag(argument.Expression, anyName));
        }

        // The MethodCache methods that take tags: invalidations, the EF Core mapping and keys.Tag(call, template)
        private static bool TakesTags(InvocationExpressionSyntax invocation)
            => (invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                SimpleNameSyntax name => name.Identifier.ValueText,
                _ => null,
            }) is "InvalidateTagAsync" or "InvalidateTagsAsync" or "InvalidateAfterCommit" or "Entity" or "Tags" or "WhenDeleted" or "Tag";

        private static bool MayHoldTag(ExpressionSyntax expression, bool anyName) => expression switch
        {
            LiteralExpressionSyntax literal => literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression),
            InterpolatedStringExpressionSyntax => true,
            IdentifierNameSyntax name => anyName || IsConstantStyle(name),
            MemberAccessExpressionSyntax { Expression: var owner, Name: IdentifierNameSyntax name } => anyName || IsConstantStyle(name) && owner is IdentifierNameSyntax or MemberAccessExpressionSyntax or AliasQualifiedNameSyntax,
            CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>().Any(e => MayHoldTag(e.Expression, anyName)),
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.Any(e => MayHoldTag(e, anyName)),
            ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions.Any(e => MayHoldTag(e, anyName)),
            InvocationExpressionSyntax call => call.ArgumentList.Arguments.Any(a => MayHoldTag(a.Expression, anyName || TakesTags(call))),
            _ => false,
        };

        // A constant is named in PascalCase; parameters and locals, which most arguments are, in camelCase
        private static bool IsConstantStyle(IdentifierNameSyntax name)
            => name.Identifier.ValueText is { Length: > 0 } text && char.IsUpper(text[0]);
    }
}
