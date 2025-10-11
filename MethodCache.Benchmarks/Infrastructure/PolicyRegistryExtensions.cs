using System;
using MethodCache.Abstractions.Registry;
using MethodCache.Core.Runtime;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Benchmarks.Infrastructure;

internal static class PolicyRegistryExtensions
{
    public static CacheRuntimeDescriptor GetSettingsFor<T>(this IPolicyRegistry registry, string methodName)
    {
        if (registry == null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentException("Method name must be provided.", nameof(methodName));
        }

        var typeName = typeof(T).FullName?.Replace('+', '.') ?? typeof(T).Name;
        var normalized = methodName;
        var genericMarker = normalized.IndexOf('<');
        if (genericMarker >= 0)
        {
            normalized = normalized[..genericMarker];
        }

        var methodId = $"{typeName}.{normalized}";
        var policyResult = registry.GetPolicy(methodId);
        return CacheRuntimeDescriptor.FromPolicyResult(policyResult);
    }
}
