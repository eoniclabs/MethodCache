using MethodCache.Abstractions.Policies;

namespace MethodCache.Core.PolicyPipeline.Model;

public sealed class CachePolicyBuilder
{
    private TimeSpan? _duration;
    private bool _durationSet;

    private readonly List<string> _tags = new();
    private bool _tagsSet;

    private Type? _keyGeneratorType;
    private bool _keyGeneratorSet;

    private int? _version;
    private bool _versionSet;

    private bool? _requireIdempotent;
    private bool _requireIdempotentSet;

    private readonly Dictionary<string, string?> _metadata = new(StringComparer.Ordinal);
    private bool _metadataSet;

    public bool HasChanges =>
        _durationSet ||
        _tagsSet ||
        _keyGeneratorSet ||
        _versionSet ||
        _requireIdempotentSet ||
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

    public CachePolicyBuilder WithKeyGenerator(Type? keyGeneratorType)
    {
        _keyGeneratorType = keyGeneratorType;
        _keyGeneratorSet = true;
        return this;
    }

    public CachePolicyBuilder WithVersion(int? version)
    {
        _version = version;
        _versionSet = true;
        return this;
    }

    public CachePolicyBuilder RequireIdempotent(bool require = true)
    {
        _requireIdempotent = require;
        _requireIdempotentSet = true;
        return this;
    }

    public CachePolicyBuilder ClearRequireIdempotent()
    {
        _requireIdempotent = null;
        _requireIdempotentSet = true;
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

        if (source._keyGeneratorSet && (overwriteExisting || !_keyGeneratorSet))
        {
            _keyGeneratorType = source._keyGeneratorType;
            _keyGeneratorSet = true;
        }

        if (source._versionSet && (overwriteExisting || !_versionSet))
        {
            _version = source._version;
            _versionSet = true;
        }

        if (source._requireIdempotentSet && (overwriteExisting || !_requireIdempotentSet))
        {
            _requireIdempotent = source._requireIdempotent;
            _requireIdempotentSet = true;
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
            _keyGeneratorType = _keyGeneratorType,
            _keyGeneratorSet = _keyGeneratorSet,
            _version = _version,
            _versionSet = _versionSet,
            _requireIdempotent = _requireIdempotent,
            _requireIdempotentSet = _requireIdempotentSet,
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

        if (_keyGeneratorSet)
        {
            policy = policy with { KeyGeneratorType = _keyGeneratorType };
            fields |= CachePolicyFields.KeyGenerator;
        }

        if (_versionSet)
        {
            policy = policy with { Version = _version };
            fields |= CachePolicyFields.Version;
        }

        if (_requireIdempotentSet)
        {
            policy = policy with { RequireIdempotent = _requireIdempotent };
            fields |= CachePolicyFields.RequireIdempotent;
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
