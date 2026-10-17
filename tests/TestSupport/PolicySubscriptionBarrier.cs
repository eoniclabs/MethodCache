using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.TestSupport;

// Test setup must wait for the native subscription, including any initial recovery callback, before
// advancing a fake clock through long factory/expiry intervals. A socket ping alone is insufficient.
internal sealed class PolicySubscriptionBarrier(IBackplane inner) : IBackplane, IAsyncDisposable
{
    private int _disposed;
    private readonly object _deliveryGate = new();
    private Func<BackplaneMessage, Task>? _callback;
    private Task _pendingDeliveries = Task.CompletedTask;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Decorate(IServiceCollection services)
    {
        var descriptor = services.Last(d => d.IsKeyedService && d.ServiceType == typeof(IBackplane) && Equals(d.ServiceKey, "MethodCache"));
        services.Remove(descriptor);
        services.AddSingleton(provider => new PolicySubscriptionBarrier((IBackplane)(descriptor.KeyedImplementationInstance
            ?? descriptor.KeyedImplementationFactory!(provider, "MethodCache"))));
        services.AddKeyedSingleton<IBackplane>("MethodCache", static (provider, _) => provider.GetRequiredService<PolicySubscriptionBarrier>());
    }
    internal static async Task WaitForNodeAsync(ServiceProvider provider)
    {
        _ = provider.GetRequiredService<ICacheManager>();
        await provider.GetRequiredService<PolicySubscriptionBarrier>().Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public string InstanceId => inner.InstanceId;
    public BackplaneState ConnectionState => inner.ConnectionState;
    public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => inner.PublishInvalidationAsync(key, cancellationToken);
    public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => inner.PublishTagInvalidationAsync(tag, cancellationToken);
    public Task PublishMethodInvalidationAsync(string methodId, CancellationToken cancellationToken = default) => inner.PublishMethodInvalidationAsync(methodId, cancellationToken);
    public Task PublishClearAsync(CancellationToken cancellationToken = default) => inner.PublishClearAsync(cancellationToken);
    public async Task SubscribeAsync(Func<BackplaneMessage, Task> callback, CancellationToken cancellationToken = default)
    {
        lock (_deliveryGate) _callback = callback;
        try { await inner.SubscribeAsync(DeliverAsync, cancellationToken); Ready.TrySetResult(); }
        catch (Exception error) { Ready.TrySetException(error); throw; }
    }
    private Task DeliverAsync(BackplaneMessage message)
    {
        lock (_deliveryGate)
        {
            if (_callback == null) return Task.CompletedTask;
            var delivery = _callback(message);
            if (!delivery.IsCompletedSuccessfully)
                _pendingDeliveries = _pendingDeliveries.IsCompleted ? delivery : Task.WhenAll(_pendingDeliveries, delivery);
            return delivery;
        }
    }
    public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (_deliveryGate)
        {
            // A native callback may already be queued before its handler is detached.
            _callback = null;
            pending = _pendingDeliveries;
        }
        await inner.UnsubscribeAsync(cancellationToken);
        await pending.WaitAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await UnsubscribeAsync();
        if (inner is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (inner is IDisposable disposable) disposable.Dispose();
    }
}
