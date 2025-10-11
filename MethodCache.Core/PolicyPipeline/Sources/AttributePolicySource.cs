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

                    var draft = BuildDraft(cacheAttribute, method, methodKey);
                    snapshots.Add(PolicySnapshotBuilder.FromPolicy(_sourceId, methodKey, draft.Policy, draft.Fields, timestamp, draft.Metadata, draft.Notes));
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

    private static PolicyDraft BuildDraft(CacheAttribute attribute, MethodInfo method, string methodKey)
    {
        var builder = new CachePolicyBuilder()
            .WithDuration(string.IsNullOrWhiteSpace(attribute.Duration)
                ? TimeSpan.FromMinutes(15)
                : TimeSpan.Parse(attribute.Duration, CultureInfo.InvariantCulture));

        if (attribute.Tags is { Length: > 0 })
        {
            builder.SetTags(attribute.Tags);
        }

        if (attribute.KeyGeneratorType != null)
        {
            builder.WithKeyGenerator(attribute.KeyGeneratorType);
        }

        if (attribute.Version >= 0)
        {
            builder.WithVersion(attribute.Version);
        }

        if (attribute.RequireIdempotent)
        {
            builder.RequireIdempotent();
        }

        if (!string.IsNullOrWhiteSpace(attribute.GroupName))
        {
            builder.AddMetadata("group", attribute.GroupName);
        }

        ApplyETagMetadata(method, builder);
        return builder.Build(methodKey);
    }

    private static void ApplyETagMetadata(MethodInfo method, CachePolicyBuilder builder)
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

        AppendETagMetadata(builder, metadata);
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

    private static void AppendETagMetadata(CachePolicyBuilder builder, ETagMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Strategy))
        {
            builder.AddMetadata("etag.strategy", metadata.Strategy);
        }

        if (metadata.IncludeParametersInETag.HasValue)
        {
            builder.AddMetadata("etag.includeParameters", metadata.IncludeParametersInETag.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (metadata.ETagGeneratorType != null)
        {
            builder.AddMetadata("etag.generatorType", metadata.ETagGeneratorType.AssemblyQualifiedName);
        }

        if (metadata.Metadata is { Length: > 0 })
        {
            builder.AddMetadata("etag.metadata", string.Join(",", metadata.Metadata));
        }

        if (metadata.UseWeakETag.HasValue)
        {
            builder.AddMetadata("etag.useWeak", metadata.UseWeakETag.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (metadata.CacheDuration.HasValue)
        {
            builder.AddMetadata("etag.cacheDuration", metadata.CacheDuration.Value.ToString());
        }
    }
}
