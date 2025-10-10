using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Sources;
using MethodCache.Core.Configuration;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Configuration.Runtime;

internal sealed class OptionsMonitorPolicySource : IPolicySource, IDisposable
{
    private readonly IOptionsMonitor<MethodCacheOptions> _optionsMonitor;
    private readonly Channel<MethodCacheOptions> _updates;
    private readonly IDisposable _subscription;
    private readonly string _sourceId;

    public OptionsMonitorPolicySource(IOptionsMonitor<MethodCacheOptions> optionsMonitor, string? sourceId = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _sourceId = sourceId ?? $"{PolicySourceIds.ConfigurationFiles}-options";

        _updates = Channel.CreateUnbounded<MethodCacheOptions>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        _subscription = _optionsMonitor.OnChange((options, _) => _updates.Writer.TryWrite(options));
    }

    public string SourceId => _sourceId;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = BuildEntries(_optionsMonitor.CurrentValue);
        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.MethodKey))
            .Select(entry => PolicySnapshotBuilder.FromConfigEntry(_sourceId, entry, timestamp))
            .ToList();

        return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots);
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => WatchChangesAsync(cancellationToken);

    private async IAsyncEnumerable<PolicyChange> WatchChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _updates.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_updates.Reader.TryRead(out var options))
            {
                var timestamp = DateTimeOffset.UtcNow;
                foreach (var entry in BuildEntries(options))
                {
                    if (string.IsNullOrWhiteSpace(entry.MethodKey))
                    {
                        continue;
                    }

                    var (policy, fields) = CachePolicyMapper.FromSettings(entry.Settings);
                    policy = CachePolicyMapper.AttachContribution(policy, _sourceId, fields, timestamp);
                    yield return PolicySnapshotBuilder.CreateChange(_sourceId, entry.MethodKey, policy, fields, PolicyChangeReason.Updated, timestamp);
                }
            }
        }
    }

    private static IReadOnlyList<MethodCacheConfigEntry> BuildEntries(MethodCacheOptions options)
    {
        var entries = new List<MethodCacheConfigEntry>();
        if (options == null)
        {
            return entries;
        }

        foreach (var serviceKvp in options.Services)
        {
            var serviceType = NormalizeServiceType(serviceKvp.Key);
            var serviceOptions = serviceKvp.Value ?? new ServiceCacheOptions();

            foreach (var methodKvp in serviceOptions.Methods)
            {
                var methodName = methodKvp.Key;
                var methodOptions = methodKvp.Value ?? new MethodOptions();

                if (methodOptions.Enabled == false)
                {
                    continue;
                }

                var settings = new CacheMethodSettings
                {
                    Duration = methodOptions.Duration
                        ?? serviceOptions.DefaultDuration
                        ?? options.DefaultDuration,
                    Tags = CombineTags(options.GlobalTags, serviceOptions.DefaultTags, methodOptions.Tags),
                    Version = methodOptions.Version
                };

                var metadata = MergeMetadata(options.ETag, serviceOptions.ETag, methodOptions.ETag);
                if (metadata != null)
                {
                    settings.SetETagMetadata(metadata);
                }

                entries.Add(new MethodCacheConfigEntry
                {
                    ServiceType = serviceType,
                    MethodName = methodName,
                    Settings = settings,
                    Priority = PolicySourcePriority.ConfigurationFiles
                });
            }
        }

        return entries;
    }

    private static List<string> CombineTags(params List<string>[] tagLists)
    {
        var merged = new HashSet<string>(StringComparer.Ordinal);

        foreach (var list in tagLists)
        {
            if (list == null)
            {
                continue;
            }

            foreach (var tag in list)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    merged.Add(tag);
                }
            }
        }

        return merged.ToList();
    }

    private static ETagMetadata? MergeMetadata(ETagOptions? global, ETagOptions? service, ETagOptions? method)
    {
        ETagMetadata? current = null;

        if (global != null)
        {
            current = ConvertToMetadata(global);
        }

        if (service != null)
        {
            current = Merge(current, ConvertToMetadata(service));
        }

        if (method != null)
        {
            current = Merge(current, ConvertToMetadata(method));
        }

        return current;
    }

    private static ETagMetadata ConvertToMetadata(ETagOptions options)
    {
        var metadata = new ETagMetadata
        {
            Strategy = options.Strategy,
            IncludeParametersInETag = options.IncludeParametersInETag,
            UseWeakETag = options.UseWeakETag,
            CacheDuration = options.CacheDuration
        };

        if (options.Metadata?.Count > 0)
        {
            metadata.Metadata = options.Metadata.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(options.ETagGeneratorType))
        {
            metadata.ETagGeneratorType = Type.GetType(options.ETagGeneratorType, throwOnError: false);
        }

        return metadata;
    }

    private static ETagMetadata Merge(ETagMetadata? baseline, ETagMetadata overlay)
    {
        var result = baseline != null ? (ETagMetadata)baseline.Clone() : new ETagMetadata();

        if (!string.IsNullOrWhiteSpace(overlay.Strategy))
        {
            result.Strategy = overlay.Strategy;
        }

        if (overlay.IncludeParametersInETag.HasValue)
        {
            result.IncludeParametersInETag = overlay.IncludeParametersInETag;
        }

        if (overlay.ETagGeneratorType != null)
        {
            result.ETagGeneratorType = overlay.ETagGeneratorType;
        }

        if (overlay.Metadata != null && overlay.Metadata.Length > 0)
        {
            result.Metadata = (string[])overlay.Metadata.Clone();
        }

        if (overlay.UseWeakETag.HasValue)
        {
            result.UseWeakETag = overlay.UseWeakETag;
        }

        if (overlay.CacheDuration.HasValue)
        {
            result.CacheDuration = overlay.CacheDuration;
        }

        return result;
    }

    private static string NormalizeServiceType(string serviceType) =>
        string.IsNullOrWhiteSpace(serviceType)
            ? string.Empty
            : serviceType.Replace('+', '.');

    public void Dispose()
    {
        _subscription.Dispose();
    }
}
