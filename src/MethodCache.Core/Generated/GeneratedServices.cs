using MethodCache.Core.Runtime.Keys;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MethodCache.Infrastructure;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Generated;

/// <summary>Container and registration-bound services for generated decorators.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedServices
{
    private readonly DecoratorServices _services;
    private readonly ConcurrentDictionary<(string Id, Type Service, string Method), CachedMethod> _methods = new();
    private readonly ConcurrentDictionary<Type, object> _sites = new();

    internal GeneratedServices(DecoratorServices services) => _services = services;
    internal static GeneratedServices From(IServiceProvider provider)
    {
        provider.GetService<MethodCacheServiceCollectionExtensions.RegistrationValidation>()?.Validate();
        return provider.GetRequiredService<GeneratedServices>();
    }

    /// <summary>Shares generated method slots once per registration and container.</summary>
    public T GetSites<T>(Func<GeneratedServices, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return (T)_sites.GetOrAdd(typeof(T), static (_, state) => state.Factory(state.Services), (Factory: factory, Services: this));
    }

    /// <summary>Binds a method to this container, service key and background-load scope target.</summary>
    public CachedMethod Method(string methodId, Type serviceType, string methodName, string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodId);
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentException.ThrowIfNullOrEmpty(methodName);
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        return _methods.GetOrAdd((methodId, serviceType, methodName), static (key, state) =>
            new CachedMethod(state.Services, PolicyHandle.For(state.Services.SitesRegistry, key.Id), key.Service, key.Method, state.Name),
            (Services: _services, Name: displayName));
    }

    /// <summary>Invalidates the supplied tag identities after a generated mutation completes.</summary>
    public Task InvalidateTagsAsync(string[] tags, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        cancellationToken.ThrowIfCancellationRequested();
        return _services.CacheManager.InvalidateByTagsAsync(tags);
    }

    /// <summary>Invalidates tags on the synchronous mutation path.</summary>
    public void InvalidateTags(string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (_services.CacheManager is InMemoryCacheManager memory) memory.InvalidateTagsSynchronously(tags);
        else throw new NotSupportedException("The configured MethodCache manager does not support synchronous invalidation. Use an async mutation method.");
    }
}

/// <summary>A bound method slot that captures one immutable policy and key snapshot per invocation.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class CachedMethod
{
    private CallSnapshot _snapshot = null!;
    internal CachedMethod(DecoratorServices services, PolicyHandle handle, Type serviceType, string methodName, string displayName)
    {
        var keyMethod = methodName + handle.KeySuffix;
        var unversioned = MethodCacheKeys.Prefix(serviceType, keyMethod);
        var background = handle.Background;
        var parameterNames = services.ParameterNames.GetValueOrDefault(handle.MethodId) ?? [];
        (handle.Shared ?? handle).Subscribe(policy => Volatile.Write(ref _snapshot,
            new CallSnapshot(services, policy, MethodCacheKeys.Prefix(serviceType, keyMethod, policy.Version), unversioned, displayName, background, parameterNames)));
    }

    /// <summary>Captures the current snapshot with one volatile read.</summary>
    public MethodCall Capture() => new(Volatile.Read(ref _snapshot));
}

internal sealed record CallSnapshot(DecoratorServices Services, CacheRuntimePolicy Policy, string Prefix,
    string KeyMethod, string Name, BackgroundLoadTarget? Background, string[] ParameterNames)
{
    private ConditionalWeakTable<string, CallSnapshot>? _specializations;
    private FastHashKeyGenerator.PreparedPrefix? _fallbackPrefix;
    internal FastHashKeyGenerator.PreparedPrefix FallbackPrefix
    {
        get
        {
            if (Volatile.Read(ref _fallbackPrefix) is { } existing) return existing;
            var created = FastHashKeyGenerator.PreparePrefix(KeyMethod, Policy.Version);
            return Interlocked.CompareExchange(ref _fallbackPrefix, created, null) ?? created;
        }
    }
    // A field, not a property: a lookup finishes the hash from it in place instead of copying the state first
    internal readonly SipHashState PrefixHash = ProcessKeyHash.Prefix(Prefix);

    // Out of line: inlined into a generated decorator, the hash used up its inlining budget, which left the hash
    // state on the stack and later calls out of line. A key builder's lookup finishes PrefixHash inline instead.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal int HashKey(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith(Prefix.AsSpan(), StringComparison.Ordinal)) return ProcessKeyHash.Compute(text);
        return PrefixHash.FinishUtf16(text[Prefix.Length..]);
    }

    internal PolicyGeneration Generation { get; } = Policy.Generation!;
    internal bool HasParameterizedTags { get; } = Policy.Tags.Any(static tag => tag.Contains('{'));

    internal CallSnapshot Specialize(string arguments)
    {
        var table = Volatile.Read(ref _specializations);
        if (table == null)
        {
            var created = new ConditionalWeakTable<string, CallSnapshot>();
            table = Interlocked.CompareExchange(ref _specializations, created, null) ?? created;
        }
        if (table.TryGetValue(arguments, out var existing)) return existing;
        return table.GetValue(arguments, value => new CallSnapshot(Services, Policy, Prefix + value,
            KeyMethod + value, Name, Background, ParameterNames));
    }
}
