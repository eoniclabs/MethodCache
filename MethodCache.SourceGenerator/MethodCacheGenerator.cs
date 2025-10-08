#nullable enable

using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    /// <summary>
    /// Source generator for MethodCache that creates decorator classes for interfaces with [Cache] and [CacheInvalidate] attributes.
    /// 
    /// Features:
    /// - Generates async/sync caching decorators for interface methods
    /// - Supports cache invalidation by tags with dynamic parameter substitution
    /// - Handles generic methods and complex type constraints
    /// - Generates dependency injection extensions
    /// - Creates centralized method registry for configuration
    /// - Proper async/await patterns for invalidation methods
    /// - Enhanced nullable type handling (int?, bool?, etc.)
    /// 
    /// Dynamic Tag Substitution:
    /// Use {parameterName} in invalidation tags to dynamically substitute method parameter values:
    /// [CacheInvalidate(Tags = new[] { "user:{userId}", "tenant:{tenantId}" })]
    /// This will generate: string.Format("user:{0}", userId?.ToString() ?? "null")
    /// 
    /// Async Patterns:
    /// - Async invalidation methods properly await the decorated method before invalidating
    /// - Cache invalidation only occurs after successful method execution
    /// - Proper ConfigureAwait(false) usage throughout generated code
    /// 
    /// Sync-over-Async Warnings:
    /// - Synchronous cached methods receive warnings about potential deadlocks
    /// - Generated code includes warnings about SynchronizationContext risks
    /// - Consider using async methods to avoid deadlock potential
    /// 
    /// Limitations:
    /// - Only works on interface methods (not class methods)
    /// - Does not support ref/out/in parameters  
    /// - Does not support void return types for caching
    /// - Does not support non-generic Task/ValueTask returns
    /// - Sync-over-async pattern may cause deadlocks in certain contexts
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed partial class MethodCacheGenerator : IIncrementalGenerator
    {
    }
}
