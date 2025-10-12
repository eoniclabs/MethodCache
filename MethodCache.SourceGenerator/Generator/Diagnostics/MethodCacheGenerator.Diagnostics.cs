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

            internal static readonly DiagnosticDescriptor SyncOverAsyncWarning = new(
                id: "MCG008",
                title: "Synchronous method caching may cause deadlocks",
                messageFormat: "Method '{0}' is synchronous but uses async caching infrastructure. This may cause deadlocks in environments with SynchronizationContext (ASP.NET Framework, WPF, WinForms). Consider making the method async or use with caution.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor DynamicTagParameterNotFound = new(
                id: "MCG009",
                title: "Dynamic tag references unknown parameter",
                messageFormat: "[CacheInvalidate] tag '{0}' references parameter '{1}' which does not exist on method '{2}'",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);
        }
    }
}
