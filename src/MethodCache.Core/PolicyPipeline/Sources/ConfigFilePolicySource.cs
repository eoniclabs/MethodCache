using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Surfaces.ConfigFile;
using MethodCache.Core.PolicyPipeline.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace MethodCache.Core.PolicyPipeline.Sources;

/// <summary>
/// Policies from an <see cref="IConfiguration"/> section such as appsettings.json. When the configuration reloads,
/// changed methods are published to the resolver, so edits take effect without a restart.
/// </summary>
internal sealed class ConfigFilePolicySource : IPolicySource
{
    private readonly IConfiguration _configuration;
    private readonly string _sectionName;
    private Dictionary<string, PolicyDraft> _policies;

    public ConfigFilePolicySource(IConfiguration configuration, string sectionName)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sectionName = sectionName ?? throw new ArgumentNullException(nameof(sectionName));

        // Invalid values fail at startup; after a reload they are reported and the previous policies stay in place
        _policies = Load();
    }

    public string SourceId => PolicySourceIds.ConfigurationFiles;

    /// <summary>Raised when a reloaded configuration has invalid values; the previous policies stay in effect.</summary>
    internal event Action<Exception>? ReloadFailed;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = Volatile.Read(ref _policies).Values
            .Select(draft => PolicySnapshotBuilder.FromPolicy(
                SourceId, draft.MethodId, draft.Policy, draft.Fields, timestamp, draft.Metadata, draft.Notes))
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots);
    }

    public async IAsyncEnumerable<PolicyChange> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reloads = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

        using var subscription = ChangeToken.OnChange(_configuration.GetReloadToken, () => reloads.Writer.TryWrite(true));

        // Each watcher diffs against what it has published, starting from the policies its snapshot saw. The
        // immediate check catches a reload that happened between that snapshot and this subscription.
        var published = Volatile.Read(ref _policies);
        reloads.Writer.TryWrite(true);

        while (await reloads.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reloads.Reader.TryRead(out _);

            Dictionary<string, PolicyDraft> current;
            try
            {
                current = Load();
            }
            catch (InvalidOperationException ex)
            {
                ReloadFailed?.Invoke(ex);
                continue;
            }

            Volatile.Write(ref _policies, current);

            foreach (var change in Diff(published, current))
            {
                yield return change;
            }

            published = current;
        }
    }

    private List<PolicyChange> Diff(Dictionary<string, PolicyDraft> previous, Dictionary<string, PolicyDraft> current)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var changes = new List<PolicyChange>();

        foreach (var (methodId, draft) in current)
        {
            var existed = previous.TryGetValue(methodId, out var old);
            if (existed && SamePolicy(old, draft))
            {
                continue;
            }

            changes.Add(new PolicyChange(
                SourceId,
                methodId,
                // Carry the same contribution as the startup snapshot, so diagnostics keep attributing it to this source
                new CachePolicyDelta(draft.Fields, CachePolicyFields.None,
                    CachePolicyMapper.AttachContribution(draft.Policy, SourceId, draft.Fields, timestamp, draft.Metadata, draft.Notes)),
                existed ? PolicyChangeReason.Updated : PolicyChangeReason.Added,
                timestamp,
                draft.Metadata));
        }

        foreach (var methodId in previous.Keys)
        {
            if (!current.ContainsKey(methodId))
            {
                changes.Add(new PolicyChange(
                    SourceId,
                    methodId,
                    new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.None, CachePolicy.Empty),
                    PolicyChangeReason.Removed,
                    timestamp));
            }
        }

        return changes;
    }

    private Dictionary<string, PolicyDraft> Load()
    {
        var policies = new Dictionary<string, PolicyDraft>(StringComparer.Ordinal);
        foreach (var draft in ConfigFilePolicySourceBuilder.Parse(_configuration, _sectionName))
        {
            var fields = draft.Fields == CachePolicyFields.None ? CachePolicyMapper.DetectFields(draft.Policy) : draft.Fields;
            policies[draft.MethodId] = draft with { Fields = fields };
        }

        return policies;
    }

    private static bool SamePolicy(PolicyDraft left, PolicyDraft right)
        => left.Fields == right.Fields &&
           string.Equals(left.Notes, right.Notes, StringComparison.Ordinal) &&
           left.Policy.Duration == right.Policy.Duration &&
           left.Policy.SlidingExpiration == right.Policy.SlidingExpiration &&
           left.Policy.RefreshAhead == right.Policy.RefreshAhead &&
           left.Policy.FailSafe == right.Policy.FailSafe &&
           left.Policy.FailSafeTimeout == right.Policy.FailSafeTimeout &&
           left.Policy.FailSafeRetryAfter == right.Policy.FailSafeRetryAfter &&
           left.Policy.Version == right.Policy.Version &&
           left.Policy.Tags.SequenceEqual(right.Policy.Tags) &&
           SameMetadata(left.Policy.Metadata, right.Policy.Metadata) &&
           SameMetadata(left.Metadata, right.Metadata);

    private static bool SameMetadata(IReadOnlyDictionary<string, string?> left, IReadOnlyDictionary<string, string?> right)
        => left.Count == right.Count &&
           left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));
}
