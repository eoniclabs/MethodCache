using MethodCache.Policies;

namespace MethodCache.Core.PolicyPipeline.Model;

internal sealed class CachePolicyBuilder
{
    private TimeSpan? _duration;
    private bool _durationSet;

    private readonly List<string> _tags = new();
    private bool _tagsSet;

    private TimeSpan? _slidingExpiration;
    private bool _slidingExpirationSet;

    private int? _version;
    private bool _versionSet;

    private TimeSpan? _refreshAhead;
    private bool _refreshAheadSet;

    private Func<object?, bool>? _cacheWhen;
    private bool _cacheWhenSet;

    private TimeSpan? _failSafe;
    private bool _failSafeSet;

    private TimeSpan? _failSafeTimeout;
    private bool _failSafeTimeoutSet;

    private TimeSpan? _failSafeRetryAfter;
    private bool _failSafeRetryAfterSet;

    private readonly Dictionary<string, string?> _metadata = new(StringComparer.Ordinal);
    private bool _metadataSet;

    public bool HasChanges =>
        _durationSet ||
        _tagsSet ||
        _slidingExpirationSet ||
        _versionSet ||
        _refreshAheadSet ||
        _cacheWhenSet ||
        _failSafeSet ||
        _failSafeTimeoutSet ||
        _failSafeRetryAfterSet ||
        _metadataSet;

    public CachePolicyBuilder WithDuration(TimeSpan? duration)
    {
        _duration = duration;
        _durationSet = true;
        return this;
    }

    public CachePolicyBuilder SetTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        _tags.Clear();
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _tags.Add(tag);
            }
        }

        _tagsSet = true;
        return this;
    }

    public CachePolicyBuilder AddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return this;
        }

        if (!_tagsSet)
        {
            _tags.Clear();
        }

        _tags.Add(tag);
        _tagsSet = true;
        return this;
    }

    public CachePolicyBuilder ClearTags()
    {
        _tags.Clear();
        _tagsSet = true;
        return this;
    }

    public CachePolicyBuilder WithSlidingExpiration(TimeSpan? slidingExpiration)
    {
        _slidingExpiration = slidingExpiration;
        _slidingExpirationSet = true;
        return this;
    }

    public CachePolicyBuilder WithVersion(int? version)
    {
        _version = version;
        _versionSet = true;
        return this;
    }

    public CachePolicyBuilder WithRefreshAhead(TimeSpan? refreshAhead)
    {
        _refreshAhead = refreshAhead;
        _refreshAheadSet = true;
        return this;
    }

    public CachePolicyBuilder WithCacheWhen(Func<object?, bool>? cacheWhen)
    {
        _cacheWhen = cacheWhen;
        _cacheWhenSet = true;
        return this;
    }

    public CachePolicyBuilder WithFailSafe(TimeSpan? failSafe)
    {
        _failSafe = failSafe;
        _failSafeSet = true;
        return this;
    }

    public CachePolicyBuilder WithFailSafeTimeout(TimeSpan? failSafeTimeout)
    {
        _failSafeTimeout = failSafeTimeout;
        _failSafeTimeoutSet = true;
        return this;
    }

    public CachePolicyBuilder WithFailSafeRetryAfter(TimeSpan? failSafeRetryAfter)
    {
        _failSafeRetryAfter = failSafeRetryAfter;
        _failSafeRetryAfterSet = true;
        return this;
    }

    public CachePolicyBuilder AddMetadata(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return this;
        }

        _metadata[key] = value;
        _metadataSet = true;
        return this;
    }

    public CachePolicyBuilder SetMetadata(IEnumerable<KeyValuePair<string, string?>> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        _metadata.Clear();
        foreach (var (key, value) in metadata)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _metadata[key] = value;
            }
        }

        _metadataSet = true;
        return this;
    }

    public CachePolicyBuilder Apply(CachePolicyBuilder source, bool overwriteExisting)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source._durationSet && (overwriteExisting || !_durationSet))
        {
            _duration = source._duration;
            _durationSet = true;
        }

        if (source._tagsSet && (overwriteExisting || !_tagsSet))
        {
            _tags.Clear();
            _tags.AddRange(source._tags);
            _tagsSet = true;
        }

        if (source._slidingExpirationSet && (overwriteExisting || !_slidingExpirationSet))
        {
            _slidingExpiration = source._slidingExpiration;
            _slidingExpirationSet = true;
        }

        if (source._versionSet && (overwriteExisting || !_versionSet))
        {
            _version = source._version;
            _versionSet = true;
        }

        if (source._refreshAheadSet && (overwriteExisting || !_refreshAheadSet))
        {
            _refreshAhead = source._refreshAhead;
            _refreshAheadSet = true;
        }

        if (source._cacheWhenSet && (overwriteExisting || !_cacheWhenSet))
        {
            _cacheWhen = source._cacheWhen;
            _cacheWhenSet = true;
        }

        if (source._failSafeSet && (overwriteExisting || !_failSafeSet))
        {
            _failSafe = source._failSafe;
            _failSafeSet = true;
        }

        if (source._failSafeTimeoutSet && (overwriteExisting || !_failSafeTimeoutSet))
        {
            _failSafeTimeout = source._failSafeTimeout;
            _failSafeTimeoutSet = true;
        }

        if (source._failSafeRetryAfterSet && (overwriteExisting || !_failSafeRetryAfterSet))
        {
            _failSafeRetryAfter = source._failSafeRetryAfter;
            _failSafeRetryAfterSet = true;
        }

        if (source._metadataSet && (overwriteExisting || !_metadataSet))
        {
            _metadata.Clear();
            foreach (var (key, value) in source._metadata)
            {
                _metadata[key] = value;
            }

            _metadataSet = true;
        }

        return this;
    }

    public CachePolicyBuilder Clone()
    {
        var clone = new CachePolicyBuilder
        {
            _duration = _duration,
            _durationSet = _durationSet,
            _slidingExpiration = _slidingExpiration,
            _slidingExpirationSet = _slidingExpirationSet,
            _version = _version,
            _versionSet = _versionSet,
            _refreshAhead = _refreshAhead,
            _refreshAheadSet = _refreshAheadSet,
            _cacheWhen = _cacheWhen,
            _cacheWhenSet = _cacheWhenSet,
            _failSafe = _failSafe,
            _failSafeSet = _failSafeSet,
            _failSafeTimeout = _failSafeTimeout,
            _failSafeTimeoutSet = _failSafeTimeoutSet,
            _failSafeRetryAfter = _failSafeRetryAfter,
            _failSafeRetryAfterSet = _failSafeRetryAfterSet,
            _metadataSet = _metadataSet,
            _tagsSet = _tagsSet
        };

        if (_tagsSet)
        {
            clone._tags.Clear();
            clone._tags.AddRange(_tags);
        }

        if (_metadataSet)
        {
            clone._metadata.Clear();
            foreach (var (key, value) in _metadata)
            {
                clone._metadata[key] = value;
            }
        }

        return clone;
    }

    public PolicyDraft Build(string methodId, string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method id must be provided.", nameof(methodId));
        }

        var (policy, fields) = BuildPolicy();
        return new PolicyDraft(methodId, policy, fields, policy.Metadata, notes);
    }

    public (CachePolicy Policy, CachePolicyFields Fields) BuildPolicy()
    {
        var policy = CachePolicy.Empty;
        var fields = CachePolicyFields.None;

        if (_durationSet)
        {
            policy = policy with { Duration = _duration };
            fields |= CachePolicyFields.Duration;
        }

        if (_tagsSet)
        {
            policy = policy with { Tags = _tags.Count == 0 ? Array.Empty<string>() : _tags.ToArray() };
            fields |= CachePolicyFields.Tags;
        }

        if (_slidingExpirationSet)
        {
            policy = policy with { SlidingExpiration = _slidingExpiration };
            fields |= CachePolicyFields.SlidingExpiration;
        }

        if (_versionSet)
        {
            policy = policy with { Version = _version };
            fields |= CachePolicyFields.Version;
        }

        if (_refreshAheadSet)
        {
            policy = policy with { RefreshAhead = _refreshAhead };
            fields |= CachePolicyFields.RefreshAhead;
        }

        if (_cacheWhenSet)
        {
            policy = policy with { CacheWhen = _cacheWhen };
            fields |= CachePolicyFields.CacheWhen;
        }

        if (_failSafeSet)
        {
            policy = policy with { FailSafe = _failSafe };
            fields |= CachePolicyFields.FailSafe;
        }

        if (_failSafeTimeoutSet)
        {
            policy = policy with { FailSafeTimeout = _failSafeTimeout };
            fields |= CachePolicyFields.FailSafeTimeout;
        }

        if (_failSafeRetryAfterSet)
        {
            policy = policy with { FailSafeRetryAfter = _failSafeRetryAfter };
            fields |= CachePolicyFields.FailSafeRetryAfter;
        }

        if (_metadataSet)
        {
            IReadOnlyDictionary<string, string?> metadataSnapshot;
            if (_metadata.Count == 0)
            {
                metadataSnapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
            }
            else
            {
                metadataSnapshot = new Dictionary<string, string?>(_metadata, StringComparer.Ordinal);
            }

            policy = policy with { Metadata = metadataSnapshot };
            fields |= CachePolicyFields.Metadata;
        }

        return (policy, fields);
    }
}
