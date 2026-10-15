using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MethodCache.Infrastructure;
using MethodCache.Extensibility;

namespace MethodCache.Generated;

/// <summary>One immutable invocation snapshot used for key construction, lookup and loading.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct MethodCall
{
    private readonly CallSnapshot _snapshot;
    internal MethodCall(CallSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>Starts an inline key using this invocation's prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KeyBuilder StartKey(Span<char> buffer)
    {
        var builder = new KeyBuilder(buffer, _snapshot);
        builder.Append(_snapshot.Prefix);
        return builder;
    }

    /// <summary>Binds already formatted key text to this invocation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KeyBuilder BindKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new KeyBuilder(key, _snapshot);
    }

    /// <summary>Binds owned key text without carrying an unused writable span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextKey BindTextKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new TextKey(key, _snapshot);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CanRead(in TextKey key)
    {
        if (!ReferenceEquals(key.Binding, _snapshot))
            throw new ArgumentException("The key belongs to a different MethodCache invocation snapshot.", nameof(key));
        return _snapshot.Policy.Enabled != false && _snapshot.Generation.IsCurrent;
    }

    /// <summary>Looks up a task from snapshot-bound owned key text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTask<T>(in TextKey key, [NotNullWhen(true)] out Task<T>? task)
    {
        if (CanRead(in key) && _snapshot.Services.FastMemory is { } memory && memory.TryGetGeneratedTextTask(key.Text, _snapshot.Generation, _snapshot.Policy.MethodId, out task))
        {
            return true;
        }
        task = null;
        return false;
    }

    /// <summary>Looks up a value from snapshot-bound owned key text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue<T>(in TextKey key, out T? value)
    {
        if (CanRead(in key) && _snapshot.Services.FastMemory is { } memory && memory.TryGetGeneratedTextValue(key.Text, _snapshot.Generation, _snapshot.Policy.MethodId, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Looks up a value task from snapshot-bound owned key text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValueTask<T>(in TextKey key, out ValueTask<T> value)
    {
        if (TryGetValue(in key, out T? found)) { value = new ValueTask<T>(found!); return true; }
        value = default;
        return false;
    }

    /// <summary>Returns this invocation's prefix for complex inline argument formatting.</summary>
    public string KeyPrefix => _snapshot.Prefix;

    /// <summary>Whether this captured policy needs argument values for its parameterized tags.</summary>
    public bool NeedsTagArguments => _snapshot.HasParameterizedTags;

    /// <summary>Adds a generic method's type identity while preserving the same policy generation.</summary>
    public MethodCall WithTypeArguments(string typeArguments)
    {
        ArgumentNullException.ThrowIfNull(typeArguments);
        return new MethodCall(_snapshot.Specialize(typeArguments));
    }

    /// <summary>Encodes arguments lacking an inline key representation using this invocation's policy.</summary>
    public KeyBuilder CreateFallbackKey(object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return BindKey(_snapshot.Services.KeyGenerator.GenerateKey(_snapshot.KeyMethod, arguments!, _snapshot.Policy));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CanRead(ref KeyBuilder key)
    {
        if (!ReferenceEquals(key.Binding, _snapshot))
            throw new ArgumentException("The key builder belongs to a different MethodCache invocation snapshot.", nameof(key));
        return _snapshot.Policy.Enabled != false && _snapshot.Generation?.IsCurrent != false;
    }

    /// <summary>Looks up a completed task without allocating a task or key on the built-in hit path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryGetTask<T>(ref KeyBuilder key, [NotNullWhen(true)] out Task<T>? task)
    {
        if (CanRead(ref key) && _snapshot.Services.FastMemory is { } memory &&
            memory.TryGetGeneratedTask(ref key, _snapshot.Generation!, _snapshot.Policy.MethodId, out task))
        {
            return true;
        }
        task = null;
        return false;
    }

    /// <summary>Looks up a value without blocking an asynchronous custom manager.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryGetValue<T>(ref KeyBuilder key, out T? value)
    {
        if (CanRead(ref key) && _snapshot.Services.FastMemory is { } memory &&
            memory.TryGetGeneratedValue(ref key, _snapshot.Generation!, _snapshot.Policy.MethodId, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Looks up an allocation-free completed value task.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryGetValueTask<T>(ref KeyBuilder key, out ValueTask<T> value)
    {
        if (TryGetValue<T>(ref key, out var found))
        {
            value = new ValueTask<T>(found!);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns the exact inner service or the corresponding service in the background load's scope.</summary>
    public TService ServiceForLoad<TService>(TService service) where TService : class
        => BackgroundLoadScope.ForLoad(_snapshot.Background, service);

    /// <summary>Loads a miss using the captured policy and a static state-based loader.</summary>
    public Task<T> LoadAsync<T, TState>(string key, TState state, Func<MethodCall, TState, Task<T>> loader,
        object?[] parameterValues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();
        var call = this;
        var telemetry = _snapshot.Services.Telemetry;
        if (_snapshot.Policy.Enabled == false || _snapshot.Policy.Generation?.IsCurrent == false)
            return telemetry?.Enabled == true ? telemetry.BypassAsync(_snapshot.Policy.MethodId, key, () => loader(call, state)) : loader(call, state);
        var policy = _snapshot.HasParameterizedTags ? CacheTagTemplates.Expand(_snapshot.Policy, _snapshot.ParameterNames, parameterValues) : _snapshot.Policy;
        if (_snapshot.Services.FastMemory == null && telemetry?.Enabled == true)
            return telemetry.CustomAsync(_snapshot.Services.CacheManager, key, policy, () => loader(call, state), cancellationToken);
        return _snapshot.Services.CacheManager.GetOrCreateFastAsync(key, _snapshot.Name,
            () => loader(call, state), policy, cancellationToken);
    }

    /// <summary>Loads a value-task miss through the same captured publication policy.</summary>
    public ValueTask<T> LoadValueAsync<T, TState>(string key, TState state, Func<MethodCall, TState, ValueTask<T>> loader,
        object?[] parameterValues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();
        var call = this;
        var telemetry = _snapshot.Services.Telemetry;
        if (_snapshot.Policy.Enabled == false || _snapshot.Policy.Generation?.IsCurrent == false)
            return telemetry?.Enabled == true ? new ValueTask<T>(telemetry.BypassAsync(_snapshot.Policy.MethodId, key, () => loader(call, state).AsTask())) : loader(call, state);
        var policy = _snapshot.HasParameterizedTags ? CacheTagTemplates.Expand(_snapshot.Policy, _snapshot.ParameterNames, parameterValues) : _snapshot.Policy;
        if (_snapshot.Services.FastMemory == null && telemetry?.Enabled == true)
            return new ValueTask<T>(telemetry.CustomAsync(_snapshot.Services.CacheManager, key, policy, () => loader(call, state).AsTask(), cancellationToken));
        return new ValueTask<T>(_snapshot.Services.CacheManager.GetOrCreateFastAsync(key, _snapshot.Name,
            () => loader(call, state).AsTask(), policy, cancellationToken));
    }

    /// <summary>Runs a synchronous factory without waiting on an asynchronous loader.</summary>
    public T Load<T, TState>(string key, TState state, Func<MethodCall, TState, T> loader,
        object?[] parameterValues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();
        var call = this;
        var telemetry = _snapshot.Services.Telemetry;
        if (_snapshot.Policy.Enabled == false || _snapshot.Policy.Generation?.IsCurrent == false)
            return telemetry?.Enabled == true ? telemetry.Bypass(_snapshot.Policy.MethodId, key, () => loader(call, state)) : loader(call, state);
        if (_snapshot.Services.FastMemory is not { } memory)
        {
            _snapshot.Services.Diagnostics.ReportSyncBypass(_snapshot.Name, "The configured custom manager has no synchronous caching capability.");
            return telemetry?.Enabled == true ? telemetry.Bypass(_snapshot.Policy.MethodId, key, () => loader(call, state)) : loader(call, state);
        }
        if (memory.HasDistributedCache)
            _snapshot.Services.Diagnostics.ReportSyncBypass(_snapshot.Name, "The distributed adapter has no bounded native synchronous operations; L2 is unavailable for this invocation.");
        var policy = _snapshot.HasParameterizedTags ? CacheTagTemplates.Expand(_snapshot.Policy, _snapshot.ParameterNames, parameterValues) : _snapshot.Policy;
        return memory.GetOrCreateSynchronously(key, _snapshot.Name, () => loader(call, state), policy, cancellationToken);
    }
}
