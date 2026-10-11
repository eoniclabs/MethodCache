#nullable enable
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Diagnostics ========================
        private static class Diagnostics
        {
            internal static readonly DiagnosticDescriptor UnsupportedVoidCache = new(
                id: "MCG001",
                title: "Caching not supported on void returns",
                messageFormat: "[Cache] attribute on method '{0}' with void return type is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedTaskCache = new(
                id: "MCG002",
                title: "Caching not supported on non-generic Task/ValueTask",
                messageFormat: "[Cache] attribute on method '{0}' with non-generic Task/ValueTask return type is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedRefParams = new(
                id: "MCG003",
                title: "Caching not supported with ref/out/in parameters",
                messageFormat: "[Cache] attribute on method '{0}' with ref/out/in parameters is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor AttributeConflict = new(
                id: "MCG004",
                title: "Conflicting cache attributes",
                messageFormat: "Method '{0}' cannot have both [Cache] and [CacheInvalidate] attributes",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor NoInvalidateTags = new(
                id: "MCG005",
                title: "CacheInvalidate has no tags",
                messageFormat: "[CacheInvalidate] on method '{0}' has no tags specified; invalidation will be a no-op",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedPointerType = new(
                id: "MCG006",
                title: "Caching not supported with pointer types",
                messageFormat: "[Cache] attribute on method '{0}' with pointer type parameters is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedRefLikeType = new(
                id: "MCG007",
                title: "Caching not supported with ref struct types",
                messageFormat: "[Cache] attribute on method '{0}' with ref struct parameters is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor DynamicTagParameterNotFound = new(
                id: "MCG009",
                title: "Dynamic tag references unknown parameter",
                messageFormat: "[CacheInvalidate] tag '{0}' references parameter '{1}' which does not exist on method '{2}'",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor CacheInterfaceUnsupportedType = new(
                id: "MCG010",
                title: "CacheInterface cannot cache this type",
                messageFormat: "[assembly: CacheInterface] cannot cache '{0}': {1}",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor CacheInterfaceMethodNotUsable = new(
                id: "MCG011",
                title: "CacheInterface lists a method it cannot cache",
                messageFormat: "[assembly: CacheInterface] cannot cache '{0}.{1}': {2}",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor CacheInterfaceDuplicate = new(
                id: "MCG012",
                title: "Interface listed in more than one CacheInterface attribute",
                messageFormat: "'{0}' is listed in more than one [assembly: CacheInterface]; list all its methods in one attribute",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);
        }
    }
}
