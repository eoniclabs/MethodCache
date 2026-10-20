using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Generated;

/// <summary>Stable bootstrap used by generated module initializers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class GeneratedCode
{
    private static readonly ConcurrentDictionary<Type, (int Version, GeneratedModule Module)> Modules = new();

    /// <summary>Records a descriptor without executing it or validating its contract version.</summary>
    public static void Register(int contractVersion, GeneratedModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        Modules[module.GetType()] = (contractVersion, module);
    }

    internal static GeneratedModuleBuilder[] Describe(IEnumerable<Assembly> explicitAssemblies, IEnumerable<Type> services)
    {
        var requested = explicitAssemblies.Distinct().ToArray();
        foreach (var assembly in requested.Concat(services.Select(static t => t.Assembly)).Distinct())
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            var name = typeof(GeneratedCode).Assembly.GetName().Name;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (!assembly.IsDynamic && assembly.GetReferencedAssemblies().Any(a => a.Name == name))
                    RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        }

        var modules = Modules.Values.Where(entry => requested.Length == 0 || requested.Contains(entry.Module.GetType().Assembly)).ToArray();
        // Validate the entire selected bootstrap before executing any descriptor or factory.
        foreach (var (version, module) in modules)
            if (version != 1)
                throw new InvalidOperationException($"MethodCache module {module.GetType().Assembly.GetName().Name} requires generator contract {version}; this runtime supports contract 1. Upgrade MethodCache to a compatible version.");
        return modules.Select(static entry =>
        {
            var builder = new GeneratedModuleBuilder(entry.Module);
            entry.Module.Describe(builder);
            return builder;
        }).ToArray();
    }

    internal static void Unregister(Type moduleType) => Modules.TryRemove(moduleType, out _);
}

/// <summary>Describes one assembly's generated services and attribute policies.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class GeneratedModule
{
    /// <summary>Initializes a module descriptor. Construction must not resolve services or policies.</summary>
    protected GeneratedModule() { }

    /// <summary>Describes the module after the runtime has validated its contract.</summary>
    public abstract void Describe(GeneratedModuleBuilder builder);
}

/// <summary>
/// Attribute data for a method. Null leaves a setting unset, so a lower layer can set it; an explicit <c>"off"</c> is
/// <see cref="TimeSpan.Zero"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedMethodOptions
{
    /// <summary>Creates empty attribute data.</summary>
    public GeneratedMethodOptions() { }
    /// <summary>The absolute duration, when explicitly specified.</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>The sliding window, when explicitly specified; zero explicitly disables it.</summary>
    public TimeSpan? SlidingExpiration { get; init; }
    /// <summary>The refresh window, when explicitly specified; zero explicitly disables it.</summary>
    public TimeSpan? RefreshAhead { get; init; }
    /// <summary>The fail-safe retention; zero explicitly disables it.</summary>
    public TimeSpan? FailSafe { get; init; }
    /// <summary>The fail-safe timeout, when explicitly specified.</summary>
    public TimeSpan? FailSafeTimeout { get; init; }
    /// <summary>The retry delay, when explicitly specified.</summary>
    public TimeSpan? FailSafeRetryAfter { get; init; }
    /// <summary>Whether caching is enabled, when explicitly specified.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Whether distributed caching is enabled, when explicitly specified.</summary>
    public bool? Distributed { get; init; }
    /// <summary>Uses canonical key-part formatting for dynamic tags; omitted by earlier contract-1 generators.</summary>
    public bool CanonicalTagParts { get; init; }
    /// <summary>The key version, when explicitly specified.</summary>
    public int? Version { get; init; }
    /// <summary>The method's static or parameterized tag templates.</summary>
    public IReadOnlyCollection<string>? Tags { get; init; }
}

/// <summary>Builds descriptor data without exposing the policy pipeline or DI implementation.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedModuleBuilder
{
    internal GeneratedModuleBuilder(GeneratedModule module) => Module = module;
    internal GeneratedModule Module { get; }
    internal List<DecoratorRegistration> Decorators { get; } = [];
    internal Dictionary<Type, List<CandidateRegistration>> Candidates { get; } = [];
    internal HashSet<Type> DeclaredServices { get; } = [];
    internal Dictionary<string, string[]> ParameterNames { get; } = new(StringComparer.Ordinal);
    internal List<PolicySnapshot> Policies { get; } = [];

    /// <summary>Records a cacheable interface definition so unsupported registrations fail explicitly.</summary>
    public void DeclareService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        DeclaredServices.Add(serviceType);
    }

    /// <summary>Describes a factory wrapping the exact inner registration with its bound runtime services.</summary>
    public void Decorator(Type serviceType, Func<object, GeneratedServices, object> factory, string[] methodIds, string[]? keyMethods = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(methodIds);
        if (keyMethods != null && keyMethods.Length != methodIds.Length) throw new ArgumentException("Each method identity needs a key method name.", nameof(keyMethods));
        var ids = methodIds.ToHashSet(StringComparer.Ordinal);
        Decorators.Add(new DecoratorRegistration(serviceType,
            (provider, inner) => factory(inner, GeneratedServices.From(provider)),
            services => AddPolicies(services, serviceType, ids))
        {
            GeneratedIn = Module.GetType().Assembly,
            MethodKeys = keyMethods == null ? [] : methodIds.Select((id, index) => (Id: id, Key: keyMethods[index])).ToArray()
        });
    }

    /// <summary>Describes an accessible concrete implementation eligible for explicit auto-registration.</summary>
    public void ImplementationCandidate(Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);
        if (!Candidates.TryGetValue(serviceType, out var list)) Candidates.Add(serviceType, list = []);
        list.Add(new CandidateRegistration(implementationType));
    }

    /// <summary>Describes a method's identifier, parameter names, group and explicit attribute settings.</summary>
    public void Method(string methodId, string[] parameterNames, string? group, GeneratedMethodOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodId);
        ArgumentNullException.ThrowIfNull(parameterNames);
        ArgumentNullException.ThrowIfNull(options);
        ParameterNames[methodId] = parameterNames.ToArray();
        var fields = CachePolicyFields.None;
        var policy = CachePolicy.Empty;
        if (options.Duration is { } duration) { policy = policy with { Duration = duration }; fields |= CachePolicyFields.Duration; }
        if (options.SlidingExpiration is { } sliding) { policy = policy with { SlidingExpiration = sliding }; fields |= CachePolicyFields.SlidingExpiration; }
        if (options.RefreshAhead is { } refresh) { policy = policy with { RefreshAhead = refresh }; fields |= CachePolicyFields.RefreshAhead; }
        if (options.FailSafe is { } failSafe) { policy = policy with { FailSafe = failSafe }; fields |= CachePolicyFields.FailSafe; }
        if (options.FailSafeTimeout is { } timeout) { policy = policy with { FailSafeTimeout = timeout }; fields |= CachePolicyFields.FailSafeTimeout; }
        if (options.FailSafeRetryAfter is { } retry) { policy = policy with { FailSafeRetryAfter = retry }; fields |= CachePolicyFields.FailSafeRetryAfter; }
        if (options.Enabled is { } enabled) { policy = policy with { Enabled = enabled }; fields |= CachePolicyFields.Enabled; }
        if (options.Distributed is { } distributed) { policy = policy with { Distributed = distributed }; fields |= CachePolicyFields.Distributed; }
        if (options.Version is { } version) { policy = policy with { Version = version }; fields |= CachePolicyFields.Version; }
        if (options.Tags is { } tags) { policy = policy with { Tags = Array.AsReadOnly(tags.ToArray()) }; fields |= CachePolicyFields.Tags; }
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal) { ["priority"] = "10" };
        if (group != null)
        {
            metadata["group"] = group;
            policy = policy with { Metadata = new Dictionary<string, string?> { ["group"] = group } };
            fields |= CachePolicyFields.Metadata;
        }
        policy = policy with { CanonicalTagParts = options.CanonicalTagParts };
        var timestamp = DateTimeOffset.UtcNow;
        policy = CachePolicyMapper.AttachContribution(policy, "attributes", fields, timestamp);
        Policies.Add(new PolicySnapshot("attributes", methodId, policy, timestamp, metadata));
    }

    private void AddPolicies(IServiceCollection services, Type serviceType, HashSet<string> methodIds)
    {
        if (services.Any(d => d.ImplementationInstance is ModulePolicyMarker marker && ReferenceEquals(marker.Module, Module) && marker.ServiceType == serviceType)) return;
        var parameters = (MethodCacheServiceCollectionExtensions.GeneratedParameters)services
            .First(d => d.ServiceType == typeof(MethodCacheServiceCollectionExtensions.GeneratedParameters)).ImplementationInstance!;
        foreach (var id in methodIds)
            if (ParameterNames.TryGetValue(id, out var names)) parameters.Names[id] = names;
        services.AddSingleton(new ModulePolicyMarker(Module, serviceType));
        services.AddSingleton(new PolicySourceRegistration(new AttributeSource(Policies.Where(p => methodIds.Contains(p.MethodId)).ToArray()), 10));
    }

    private sealed record ModulePolicyMarker(GeneratedModule Module, Type ServiceType);
    private sealed class AttributeSource(PolicySnapshot[] snapshots) : IPolicySource
    {
        public string SourceId => "attributes";
        public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots);
        }
        public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
            => PolicySourceAsyncEnumerable.Empty(cancellationToken);
    }
}

internal sealed class CandidateRegistration
{
    internal CandidateRegistration([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType)
        => ImplementationType = implementationType;
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    internal Type ImplementationType { get; }
}
