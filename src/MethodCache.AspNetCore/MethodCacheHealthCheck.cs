using MethodCache.Core.Runtime.Http;
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
            var http = _services.GetService<HttpResponseStore>();
            var data = new Dictionary<string, object>
            {
                ["managerCapability"] = builtIn ? "built_in" : manager is NoOpCacheManager ? "noop" : "custom",
                ["statisticsAvailable"] = (builtIn || http != null) && admin != null,
                ["methodStatisticsAvailable"] = builtIn && admin != null,
                ["httpCapability"] = http == null ? "not_configured" : http.Distributed ? "distributed" : "local"
            };
            if ((builtIn || http != null) && admin != null)
            {
                var stats = await admin.GetStatsAsync(cancellationToken).ConfigureAwait(false);
                if (builtIn)
                {
                    data["localMethodEntries"] = stats.LocalMethodEntries;
                    data["totalWeight"] = stats.TotalWeight;
                    data["evictions"] = stats.Evictions;
                }
                data["localHttpEntries"] = stats.LocalHttpEntries;
                data["localHttpBytes"] = stats.LocalHttpBytes;
                data["observedDistributedEntries"] = stats.ObservedDistributedEntries;
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
            if (transport?.HasIncompatibleScope == true)
                return HealthCheckResult.Unhealthy("The distributed cache scope has an incompatible schema or retention contract. This node uses memory only; repair the configuration and restart, or use a new KeyPrefix.", data: data);
            var degraded = distributed == false || queues.Distributed != 0 || queues.Publication != 0 ||
                state is BackplaneState.Unknown or BackplaneState.Connecting or BackplaneState.Disconnected;
            if (!degraded && transport?.HasRetentionMismatch == true)
                return HealthCheckResult.Degraded("TagRetention differs from the established scope contract; MethodCache uses the established retention.", data: data);
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
