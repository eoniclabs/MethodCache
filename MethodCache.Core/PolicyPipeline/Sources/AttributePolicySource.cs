using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Sources;

public sealed class AttributePolicySource : IPolicySource
{
    private readonly Assembly[] _assemblies;
    private readonly string _sourceId;

    public AttributePolicySource(params Assembly[] assemblies)
    {
        _sourceId = PolicySourceIds.Attributes;
        _assemblies = assemblies is { Length: > 0 }
            ? assemblies
            : new[] { Assembly.GetCallingAssembly() };
    }

    public string SourceId => _sourceId;

    public async Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>();

        foreach (var assembly in _assemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(static t => t != null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var cacheAttribute = method.GetCustomAttribute<CacheAttribute>();
                    if (cacheAttribute == null)
                    {
                        continue;
                    }

                    var methodKey = BuildMethodKey(type, method);
                    if (string.IsNullOrWhiteSpace(methodKey))
                    {
                        continue;
                    }

                    var settings = BuildSettings(cacheAttribute, method);
                    var (policy, fields) = CachePolicyMapper.FromSettings(settings);

                    IReadOnlyDictionary<string, string?>? metadata = null;
                    if (!string.IsNullOrWhiteSpace(cacheAttribute.GroupName))
                    {
                        metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["group"] = cacheAttribute.GroupName
                        };
                    }

                    snapshots.Add(PolicySnapshotBuilder.FromPolicy(_sourceId, methodKey, policy, fields, timestamp, metadata));
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return snapshots;
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);

    private static string BuildMethodKey(Type declaringType, MethodInfo method)
    {
        if (declaringType == null || method == null)
        {
            return string.Empty;
        }

        var typeName = declaringType.FullName ?? declaringType.Name;
        typeName = typeName.Replace('+', '.');
        return $"{typeName}.{method.Name}";
    }

    private static CacheMethodSettings BuildSettings(CacheAttribute attribute, MethodInfo method)
    {
        var settings = new CacheMethodSettings
        {
            Duration = string.IsNullOrWhiteSpace(attribute.Duration)
                ? TimeSpan.FromMinutes(15)
                : TimeSpan.Parse(attribute.Duration, CultureInfo.InvariantCulture),
            Tags = attribute.Tags?.ToList() ?? new List<string>(),
            Version = attribute.Version >= 0 ? attribute.Version : null,
            KeyGeneratorType = attribute.KeyGeneratorType,
            IsIdempotent = attribute.RequireIdempotent
        };

        LoadETagSettings(method, settings);
        return settings;
    }

    private static void LoadETagSettings(MethodInfo method, CacheMethodSettings settings)
    {
        var etagAttributeType = Type.GetType("MethodCache.ETags.Attributes.ETagAttribute, MethodCache.ETags");
        if (etagAttributeType == null)
        {
            return;
        }

        var etagAttribute = method.GetCustomAttribute(etagAttributeType);
        if (etagAttribute == null)
        {
            return;
        }

        var metadata = new ETagMetadata
        {
            Strategy = etagAttributeType.GetProperty("Strategy")?.GetValue(etagAttribute)?.ToString(),
            IncludeParametersInETag = GetNullableValue<bool>(etagAttributeType, etagAttribute, "IncludeParametersInETag"),
            ETagGeneratorType = etagAttributeType.GetProperty("ETagGeneratorType")?.GetValue(etagAttribute) as Type,
            Metadata = etagAttributeType.GetProperty("Metadata")?.GetValue(etagAttribute) as string[],
            UseWeakETag = GetNullableValue<bool>(etagAttributeType, etagAttribute, "UseWeakETag")
        };

        var cacheDurationMinutes = GetNullableValue<int>(etagAttributeType, etagAttribute, "CacheDurationMinutes");
        if (cacheDurationMinutes.HasValue)
        {
            metadata.CacheDuration = TimeSpan.FromMinutes(cacheDurationMinutes.Value);
        }

        settings.SetETagMetadata(metadata);
    }

    private static T? GetNullableValue<T>(Type attributeType, object attribute, string propertyName) where T : struct
    {
        var property = attributeType.GetProperty(propertyName);
        if (property == null)
        {
            return null;
        }

        var value = property.GetValue(attribute);
        if (value == null)
        {
            return null;
        }

        if (value is T typed)
        {
            return typed;
        }

        if (value.GetType() == typeof(T?))
        {
            var nullable = (T?)value;
            return nullable.HasValue ? nullable.Value : null;
        }

        return null;
    }
}
