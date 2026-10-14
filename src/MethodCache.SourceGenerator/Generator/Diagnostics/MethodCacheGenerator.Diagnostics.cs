#nullable enable
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Diagnostics ========================
        private static class Diagnostics
        {
            // Each id has its own heading in the diagnostics guide, so IDEs and build output link straight to the fix
            private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

            internal static readonly DiagnosticDescriptor UnsupportedVoidCache = new(
                id: "MCG001",
                title: "Caching not supported on void returns",
                messageFormat: "[Cache] on '{0}' does nothing: a void method has no result to cache. Return a value, or remove [Cache].",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG001"));

            internal static readonly DiagnosticDescriptor UnsupportedTaskCache = new(
                id: "MCG002",
                title: "Caching not supported on non-generic Task/ValueTask",
                messageFormat: "[Cache] on '{0}' does nothing: a Task or ValueTask without a result has nothing to cache. Return Task<T> or ValueTask<T>, or remove [Cache].",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG002"));

            internal static readonly DiagnosticDescriptor UnsupportedRefParams = new(
                id: "MCG003",
                title: "Caching not supported with reference parameters or returns",
                messageFormat: "[Cache] on '{0}' is not supported: reference parameters and reference returns cannot be cached. Pass and return values by value, or remove [Cache].",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG003"));

            internal static readonly DiagnosticDescriptor AttributeConflict = new(
                id: "MCG004",
                title: "Conflicting cache attributes",
                messageFormat: "'{0}' has both [Cache] and [CacheInvalidate]. Keep one of them.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG004"));

            internal static readonly DiagnosticDescriptor NoInvalidateTags = new(
                id: "MCG005",
                title: "CacheInvalidate has no tags",
                messageFormat: "[CacheInvalidate] on '{0}' removes nothing because it has no tags. Add Tags = new[] {{ \"...\" }} with the tags of the cached methods it should clear.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG005"));

            internal static readonly DiagnosticDescriptor UnsupportedPointerType = new(
                id: "MCG006",
                title: "Caching not supported with pointer types",
                messageFormat: "[Cache] on '{0}' is not supported: pointer and function pointer parameters or returns, including arrays of them, cannot be cached. Use a value or an array of values, or remove [Cache].",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG006"));

            internal static readonly DiagnosticDescriptor UnsupportedRefLikeType = new(
                id: "MCG007",
                title: "Caching not supported with ref struct types",
                messageFormat: "[Cache] on '{0}' is not supported: ref struct values such as Span<T>, or type parameters that allow ref structs, cannot be cached as arguments or returns. Use an array, Memory<T> or a string, or remove [Cache].",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG007"));

            internal static readonly DiagnosticDescriptor DynamicTagParameterNotFound = new(
                id: "MCG009",
                title: "Dynamic tag references unknown parameter",
                messageFormat: "Tag '{0}' names parameter '{1}', which '{2}' does not have, so it stays literal text. Put one of the method's parameter names between the braces.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG009"));

            internal static readonly DiagnosticDescriptor AmbiguousTagParameter = new(
                id: "MCG016",
                title: "Tag placeholder matches multiple parameters",
                messageFormat: "Tag '{0}' names '{1}', which matches multiple parameters of '{2}' when ignoring case, so it stays literal text. Use the exact spelling of the intended parameter.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG016"));

            internal static readonly DiagnosticDescriptor MalformedTagPlaceholder = new(
                id: "MCG015",
                title: "Tag placeholder is not a parameter name",
                messageFormat: "Tag '{0}' on '{1}' contains '{{{2}}}', which is not a parameter name, so it stays literal text. Put only a parameter name between the braces; to tag by a property such as product.Id, add the value as its own parameter.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG015"));

            internal static readonly DiagnosticDescriptor CacheInterfaceUnsupportedType = new(
                id: "MCG010",
                title: "CacheInterface cannot cache this type",
                messageFormat: "[assembly: CacheInterface] cannot cache '{0}': {1}",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG010"));

            internal static readonly DiagnosticDescriptor CacheInterfaceMethodNotUsable = new(
                id: "MCG011",
                title: "CacheInterface lists a method it cannot cache",
                messageFormat: "[assembly: CacheInterface] cannot cache '{0}.{1}': {2}",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG011"));

            internal static readonly DiagnosticDescriptor CacheInterfaceDuplicate = new(
                id: "MCG012",
                title: "Interface listed in more than one CacheInterface attribute",
                messageFormat: "'{0}' is listed in more than one [assembly: CacheInterface]; list all its methods in one attribute",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG012"));

            internal static readonly DiagnosticDescriptor UnsupportedLazySequence = new(
                id: "MCG014",
                title: "Cannot cache a lazy sequence",
                messageFormat: "[Cache] on '{0}' would cache the {1}, not its items, so the work would run on every enumeration; return a materialized type such as List<T> or T[]",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG014"));

            internal static readonly DiagnosticDescriptor NotInterceptableMember = new(
                id: "MCG017",
                title: "Cache attribute on a member the decorator can't intercept",
                messageFormat: "[{0}] on '{1}' does nothing: the decorator only implements public, non-static members that a class can implement, and this one is {2}. Make it a public interface method without 'static', 'private' or 'sealed', or remove the attribute.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG017"));

            internal static readonly DiagnosticDescriptor FrameworkTypeWithoutKeyForm = new(
                id: "MCG018",
                title: "Cache key argument has a framework type without a key form",
                messageFormat: "[Cache] on '{0}' is not supported: '{1}' is a '{2}', a framework type the cache key can't be written from, so different values could share a key. Pass a value that identifies it, such as a string or an id, or wrap it in a type that implements ICacheKeyProvider.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG018"));

            internal static readonly DiagnosticDescriptor UnrestorableResultType = new(
                id: "MCG019",
                title: "Cached result type can't be read back from a distributed cache",
                messageFormat: "'{0}' returns '{1}', and '{2}' is not a concrete type: when a distributed cache is used, the serializer can't restore the runtime type from it, and with the default round-trip check these results are kept in memory only. Return a concrete type to share them through the distributed cache.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG019"));

            internal static readonly DiagnosticDescriptor UnsupportedInterface = new(
                id: "MCG013",
                title: "Interface cannot be cached",
                messageFormat: "'{0}' cannot be cached: {1}",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MCG013"));
        }
    }
}
