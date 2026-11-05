using System.Reflection;
using MethodCache.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Testing;

/// <summary>
/// Checks that every tag of a cached method has something that invalidates it, across the whole application, where
/// analyzer MC0028 sees one project at a time.
/// </summary>
/// <example>
/// <code>
/// var mapping = provider.GetRequiredService&lt;EntityTagMapping&lt;ShopDb&gt;&gt;();
/// Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: [.. mapping.AllTags, "food-catalog"]));
/// </code>
/// </example>
public static class TagCoverage
{
    /// <summary>
    /// The tags of the cached methods in <paramref name="provider"/>, from attributes, startup code and configuration,
    /// that neither a <c>[CacheInvalidate]</c> method of a registered service nor <paramref name="alsoInvalidatedBy"/>
    /// invalidates. An empty list means every tag is covered.
    /// </summary>
    /// <param name="provider">The application's service provider, built as in the application.</param>
    /// <param name="alsoInvalidatedBy">
    /// The tags your code invalidates in ways a container can't see, such as <c>InvalidateTagAsync</c> calls, another
    /// application's invalidations, and the tags of an Entity Framework Core mapping (<c>EntityTagMapping&lt;TContext&gt;.AllTags</c>).
    /// A placeholder matches any value, so <c>"product:{id}"</c> covers <c>"product:{Id}"</c> and <c>"product:42"</c>.
    /// </param>
    /// <returns>Each tag that nothing invalidates, with the method it is on.</returns>
    public static IReadOnlyList<UninvalidatedTag> Uninvalidated(IServiceProvider provider, IEnumerable<string>? alsoInvalidatedBy = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var covered = InvalidatedByServices(provider).Concat(alsoInvalidatedBy ?? [])
            .Select(TagPattern.Of).Distinct(StringComparer.Ordinal).ToArray();
        var uninvalidated = new List<UninvalidatedTag>();
        foreach (var method in provider.GetRequiredService<ICacheAdministrator>().GetMethods().OrderBy(static m => m.MethodId, StringComparer.Ordinal))
        {
            foreach (var tag in method.Options.Tags)
            {
                var pattern = TagPattern.Of(tag);
                if (!covered.Any(invalidated => TagPattern.Overlaps(pattern, invalidated))) uninvalidated.Add(new UninvalidatedTag(tag, method.MethodId));
            }
        }
        return uninvalidated;
    }

    /// <summary>The tags of the <c>[CacheInvalidate]</c> methods on the services registered for caching.</summary>
    private static IEnumerable<string> InvalidatedByServices(IServiceProvider provider)
    {
        var registered = provider.GetService<MethodCacheServiceCollectionExtensions.RegistrationValidation>()?.Accepted ?? [];
        return registered.Select(static descriptor => descriptor.ServiceType).Distinct()
            .SelectMany(static service => service.GetMethods().Concat(service.GetInterfaces().SelectMany(static inherited => inherited.GetMethods())))
            .SelectMany(static method => method.GetCustomAttribute<CacheInvalidateAttribute>()?.Tags ?? []);
    }
}

/// <summary>A tag of a cached method that nothing invalidates, as <see cref="TagCoverage.Uninvalidated"/> reports it.</summary>
/// <param name="Tag">The tag, as written on the method or in its configuration, such as <c>"product:{id}"</c>.</param>
/// <param name="MethodId">The id of the cached method, as <see cref="CachedMethodInfo.MethodId"/> gives it.</param>
public sealed record UninvalidatedTag(string Tag, string MethodId)
{
    /// <summary>The tag and its method, such as <c>product:{id} on [MyApp]MyApp.IProductService.GetAsync</c>.</summary>
    public override string ToString() => $"{Tag} on {MethodId}";
}
