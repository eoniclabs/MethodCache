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

            internal static readonly DiagnosticDescriptor InvalidDuration = new(
                "MC0004", "Invalid duration", "{0} = \"{1}\" is not a duration; write {2}",
                "Usage", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink("MC0004"));

            internal static readonly DiagnosticDescriptor InvalidVersion = new(
                "MC0021", "Invalid cache version", "Version must be non-negative when explicitly specified",
                "Usage", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink("MC0021"));

            internal static readonly DiagnosticDescriptor RefreshAheadNotShorterThanDuration = new(
                "MC0023", "RefreshAhead is not shorter than Duration or SlidingExpiration",
                "RefreshAhead = \"{0}\" is not shorter than {1} = \"{2}\", so every hit would reload the value; use a RefreshAhead window shorter than the {1}",
                "Usage", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink("MC0023"));

            internal static readonly DiagnosticDescriptor SlidingNotShorterThanDuration = new(
                "MC0038", "SlidingExpiration is not shorter than Duration",
                "SlidingExpiration = \"{0}\" is not shorter than Duration = \"{1}\", so reads never extend an entry: each one expires {1} after it was loaded. Use a sliding window shorter than the Duration, or remove SlidingExpiration.",
                "Usage", DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink("MC0038"));

            internal static readonly DiagnosticDescriptor UnsupportedVoidCache = new(
                id: "MC0003",
                title: "Caching not supported on void returns",
                messageFormat: "[Cache] on '{0}' does nothing: a void method has no result to cache. Return a value, or remove [Cache].",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0003"));

            internal static readonly DiagnosticDescriptor UnsupportedTaskCache = new(
                id: "MC0022",
                title: "Caching not supported on non-generic Task/ValueTask",
                messageFormat: "[Cache] on '{0}' does nothing: a Task or ValueTask without a result has nothing to cache. Return Task<T> or ValueTask<T>, or remove [Cache].",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0022"));

            internal static readonly DiagnosticDescriptor UnsupportedRefParams = new(
                id: "MC0005",
                title: "Caching not supported with reference parameters or returns",
                messageFormat: "[Cache] on '{0}' is not supported: reference parameters and reference returns cannot be cached. Pass and return values by value, or remove [Cache].",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0005"));

            internal static readonly DiagnosticDescriptor AttributeConflict = new(
                id: "MC0006",
                title: "Conflicting cache attributes",
                messageFormat: "'{0}' has both [Cache] and [CacheInvalidate]. Keep one of them.",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0006"));

            internal static readonly DiagnosticDescriptor NoInvalidateTags = new(
                id: "MC0007",
                title: "CacheInvalidate has no tags",
                messageFormat: "[CacheInvalidate] on '{0}' removes nothing because it has no tags. Add Tags = new[] {{ \"...\" }} with the tags of the cached methods it should clear.",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0007"));

            internal static readonly DiagnosticDescriptor UnsupportedPointerType = new(
                id: "MC0008",
                title: "Caching not supported with pointer types",
                messageFormat: "[Cache] on '{0}' is not supported: pointer and function pointer parameters or returns, including arrays of them, cannot be cached. Use a value or an array of values, or remove [Cache].",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0008"));

            internal static readonly DiagnosticDescriptor UnsupportedRefLikeType = new(
                id: "MC0009",
                title: "Caching not supported with ref struct types",
                messageFormat: "[Cache] on '{0}' is not supported: ref struct values such as Span<T>, or type parameters that allow ref structs, cannot be cached as arguments or returns. Use an array, Memory<T> or a string, or remove [Cache].",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0009"));

            internal static readonly DiagnosticDescriptor DynamicTagParameterNotFound = new(
                id: "MC0010",
                title: "Dynamic tag references unknown parameter",
                messageFormat: "Tag '{0}' names parameter '{1}', which '{2}' does not have, so it stays literal text. Put one of the method's parameter names between the braces.",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0010"));

            internal static readonly DiagnosticDescriptor AmbiguousTagParameter = new(
                id: "MC0017",
                title: "Tag placeholder matches multiple parameters",
                messageFormat: "Tag '{0}' names '{1}', which matches multiple parameters of '{2}' when ignoring case, so it stays literal text. Use the exact spelling of the intended parameter.",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0017"));

            internal static readonly DiagnosticDescriptor MalformedTagPlaceholder = new(
                id: "MC0016",
                title: "Tag placeholder is not a parameter name",
                messageFormat: "Tag '{0}' on '{1}' contains '{{{2}}}', which is not a parameter name, so it stays literal text. Put only a parameter name between the braces; to tag by a property such as product.Id, add the value as its own parameter.",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0016"));

            internal static readonly DiagnosticDescriptor CacheInterfaceUnsupportedType = new(
                id: "MC0011",
                title: "CacheInterface cannot cache this type",
                messageFormat: "[assembly: CacheInterface] cannot cache '{0}': {1}",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0011"));

            internal static readonly DiagnosticDescriptor CacheInterfaceMethodNotUsable = new(
                id: "MC0012",
                title: "CacheInterface lists a method it cannot cache",
                messageFormat: "[assembly: CacheInterface] cannot cache '{0}.{1}': {2}",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0012"));

            internal static readonly DiagnosticDescriptor CacheInterfaceDuplicate = new(
                id: "MC0013",
                title: "Interface listed in more than one CacheInterface attribute",
                messageFormat: "'{0}' is listed in more than one [assembly: CacheInterface]; list all its methods in one attribute",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0013"));

            internal static readonly DiagnosticDescriptor UnsupportedLazySequence = new(
                id: "MC0015",
                title: "Cannot cache a lazy sequence",
                messageFormat: "[Cache] on '{0}' would cache the {1}, not its items, so the work would run on every enumeration; return a materialized type such as List<T> or T[]",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0015"));

            internal static readonly DiagnosticDescriptor NotInterceptableMember = new(
                id: "MC0018",
                title: "Cache attribute on a member the decorator can't intercept",
                messageFormat: "[{0}] on '{1}' does nothing: the decorator only implements public, non-static members that a class can implement, and this one is {2}. Make it a public interface method without 'static', 'private' or 'sealed', or remove the attribute.",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0018"));

            internal static readonly DiagnosticDescriptor FrameworkTypeWithoutKeyForm = new(
                id: "MC0019",
                title: "Cache key argument has a framework type without a key form",
                messageFormat: "[Cache] on '{0}' is not supported: '{1}' is a '{2}', a framework type the cache key can't be written from, so different values could share a key. Pass a value that identifies it, such as a string or an id, or wrap it in a type that implements ICacheKeyProvider.",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0019"));

            internal static readonly DiagnosticDescriptor HiddenStateKeyType = new(
                id: "MC0034",
                title: "Cache key argument has a type whose state isn't public",
                messageFormat: "[Cache] on '{0}' is not supported: '{1}' is a '{2}', whose state is not public, so every value would get the same key and callers would get each other's results. Implement ICacheKeyProvider on the type, or give it public properties that identify the value.",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0034"));

            internal static readonly DiagnosticDescriptor UnrestorableResultType = new(
                id: "MC0020",
                title: "Cached result type can't be read back from a distributed cache",
                messageFormat: "'{0}' returns '{1}', and '{2}' is not a concrete type: when a distributed cache is used, the serializer can't restore the runtime type from it, and with the default round-trip check these results are kept in memory only. Return a concrete type, or name its derived types with [JsonDerivedType], to share them through the distributed cache.",
                category: "Performance",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0020"));

            internal static readonly DiagnosticDescriptor UnregistrableDerivedType = new(
                id: "MC0039",
                title: "Derived type can't be restored from a distributed cache",
                messageFormat: "'{0}' derives from '{1}', which a cached result holds, but {2}, so MethodCache can't name it to the distributed cache's serializer, and a result holding one stays in each instance's memory. Make it a public or internal class that isn't generic or nested in a private type, or name it with [JsonDerivedType] on '{1}'.",
                category: "Performance",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0039"));

            internal static readonly DiagnosticDescriptor UnlistedDerivedType = new(
                id: "MC0040",
                title: "Derived type missing from the JsonSerializerContext passed to UseJson",
                messageFormat: "'{0}' has no contract for '{1}', which derives from '{2}' that a cached result holds, so the distributed cache can't restore it as itself, and a value holding one stays in each instance's memory. Add [JsonSerializable(typeof({1}))] to '{0}'.",
                category: "Performance",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0040"));

            internal static readonly DiagnosticDescriptor LazyLoadedEntityResult = new(
                id: "MC0041",
                title: "Cached result holds entities that EF Core's lazy-loading proxies create",
                messageFormat: "'{0}' returns '{1}', which holds '{2}', an entity with virtual navigation properties, and the project uses EF Core's lazy-loading proxies: a proxy can't be restored from the distributed cache, so these results stay in each instance's memory, and writing one for the cache can load its navigations from the database. Return a type without virtual navigations, such as a record the query projects with Select.",
                category: "Performance",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0041"));

            internal static readonly DiagnosticDescriptor HiddenStateResultType = new(
                id: "MC0042",
                title: "Cached result type keeps its state where serializers don't write it",
                messageFormat: "'{0}' returns '{1}', and '{2}' keeps its state in non-public fields or properties only, so a serializer writes it as an empty object and it would come back empty from the distributed cache. Give it public properties, mark its fields [JsonInclude], or give it a JSON converter.",
                category: "Reliability",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0042"));

            internal static readonly DiagnosticDescriptor RuntimeKeyArgument = new(
                id: "MC0037",
                title: "Cache key is written when the call runs",
                messageFormat: "The key of '{0}' is written when the call runs: '{1}' is a '{2}', so the key generator reads the argument's actual type and hashes it. The key is right, but slower to build than one written at build time, and not readable. To have it written at build time, pass simple values or a sealed record of them.",
                category: "Performance",
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0037"));

            internal static readonly DiagnosticDescriptor SharedUnsafeResultType = new(
                id: "MC0025",
                title: "Cached result type isn't safe to share between threads",
                messageFormat: "'{0}' returns '{1}', and '{2}' is not safe to share: the cache hands the same instance to every caller at once, and this type changes as it is read or used. Return an immutable type, such as a record or a string, instead.",
                category: "Reliability",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0025"));

            internal static readonly DiagnosticDescriptor UnsupportedInterface = new(
                id: "MC0014",
                title: "Interface cannot be cached",
                messageFormat: "'{0}' cannot be cached: {1}",
                category: "Design",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                helpLinkUri: HelpLink("MC0014"));
        }
    }
}
