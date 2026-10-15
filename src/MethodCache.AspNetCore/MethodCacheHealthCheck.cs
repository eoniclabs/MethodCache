using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.AspNetCore;

/// <summary>Reports configured cache capabilities, local statistics, recovery queues and transport health.</summary>
public sealed class MethodCacheHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;

    /// <summary>Resolves optional administration and owned transport capabilities from this container.</summary>
    public MethodCacheHealthCheck(IServiceProvider services) => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = _services.GetService<ICacheManager>();
            if (manager == null) return new HealthCheckResult(context.Registration.FailureStatus, "MethodCache is not configured.");
            var admin = _services.GetService<ICacheAdministrator>();
            var transport = _services.GetService<DistributedCacheLayer>();
            var builtIn = manager is InMemoryCacheManager;
            var data = new Dictionary<string, object>
            {
                ["managerCapability"] = builtIn ? "built_in" : manager.GetType() == typeof(NoOpCacheManager) ? "noop" : "custom",
                ["statisticsAvailable"] = builtIn && admin != null
            };
            if (builtIn && admin != null)
            {
                var stats = await admin.GetStatsAsync(cancellationToken).ConfigureAwait(false);
                data["localMethodEntries"] = stats.LocalMethodEntries;
                data["localHttpEntries"] = stats.LocalHttpEntries;
                data["localHttpBytes"] = stats.LocalHttpBytes;
                data["observedDistributedEntries"] = stats.ObservedDistributedEntries;
                data["totalWeight"] = stats.TotalWeight;
                data["evictions"] = stats.Evictions;
            }
            var queues = transport?.GetInvalidationQueues() ?? admin?.GetInvalidationQueues() ?? new InvalidationQueues(0, 0);
            data["invalidationQueued"] = queues.Distributed;
            data["publicationQueued"] = queues.Publication;
            var state = transport?.BackplaneStatus ?? BackplaneState.NotConfigured;
            data["backplaneStatus"] = StateName(state);
            bool? distributed = transport != null
                ? await transport.CheckAvailableAsync(cancellationToken).ConfigureAwait(false)
                : admin != null ? await admin.CheckDistributedCacheAsync(cancellationToken).ConfigureAwait(false) : null;
            if (distributed.HasValue) data["distributedCacheAvailable"] = distributed.Value;
            var degraded = distributed == false || queues.Distributed != 0 || queues.Publication != 0 ||
                state is BackplaneState.Unknown or BackplaneState.Connecting or BackplaneState.Disconnected;
            return degraded
                ? new HealthCheckResult(context.Registration.FailureStatus, "Cache transport or invalidation delivery is unavailable or recovering.", data: data)
                : HealthCheckResult.Healthy("MethodCache is available.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages can contain keys, parameters or backend credentials.
            return new HealthCheckResult(context.Registration.FailureStatus, "MethodCache did not answer.", data: new Dictionary<string, object> { ["error.type"] = ex.GetType().FullName ?? ex.GetType().Name });
        }
    }

    private static string StateName(BackplaneState state) => state switch
    {
        BackplaneState.NotConfigured => "not_configured",
        BackplaneState.Connecting => "connecting",
        BackplaneState.Connected => "connected",
        BackplaneState.Disconnected => "disconnected",
        _ => "unknown"
    };
}
