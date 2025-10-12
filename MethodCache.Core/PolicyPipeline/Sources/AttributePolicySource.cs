using System.Globalization;
using System.Reflection;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core;
using MethodCache.Core.Configuration.Surfaces.ConfigFile;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.PolicyPipeline.Sources;

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

        return builder.Build(methodKey);
    }
}
