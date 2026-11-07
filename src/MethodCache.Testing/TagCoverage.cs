using System.Reflection;
using MethodCache.Core.Management;
using MethodCache.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Testing;

/// <summary>
/// Checks that every tag of a cached method has something that invalidates it, across the whole application, where
/// analyzer MC0028 sees one project at a time.
/// </summary>
/// <example>
/// <code>
/// Assert.Empty(TagCoverage.Uninvalidated(provider));
/// </code>
/// </example>
public static class TagCoverage
{
    /// <summary>
    /// The tags of the cached methods in <paramref name="provider"/>, from attributes, startup code and configuration,
    /// that nothing invalidates. A tag is covered by a <c>[CacheInvalidate]</c> method of a registered service, by the
    /// entity mapping of <c>mc.UseEntityFrameworkCore</c>, by an invalidation in a project the tests reference, or by
    /// <paramref name="alsoInvalidatedBy"/>. An empty list means every tag is covered.
    /// </summary>
    /// <remarks>
    /// MethodCache's generator writes the tags each project invalidates into its assembly: <c>[CacheInvalidate]</c>
    /// tags and the tags its code passes to <c>InvalidateTagAsync</c>, <c>InvalidateTagsAsync</c>,
    /// <c>InvalidateAfterCommit</c> or an <c>[InvalidatesCache]</c> helper. They are read from the assemblies next to
    /// the tests, so reference the projects that invalidate, such as a worker, from the test project. A tag in
    /// <c>InvalidatedElsewhere</c> doesn't cover itself: it is reported, with
    /// <see cref="UninvalidatedTag.MarkedInvalidatedElsewhere"/> set, when no referenced project invalidates it.
    /// Invalidations in test projects don't count.
    /// </remarks>
    /// <param name="provider">The application's service provider, built as in the application.</param>
    /// <param name="alsoInvalidatedBy">
    /// The tags invalidated where no referenced project shows it, such as by another application or a database trigger.
    /// A placeholder matches any value, so <c>"product:{id}"</c> covers <c>"product:{Id}"</c> and <c>"product:42"</c>.
    /// </param>
    /// <returns>Each tag that nothing invalidates, with the method it is on.</returns>
    public static IReadOnlyList<UninvalidatedTag> Uninvalidated(IServiceProvider provider, IEnumerable<string>? alsoInvalidatedBy = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var services = RegisteredServices(provider);
        var covered = InvalidatedBy(services)
            .Concat(provider.GetServices<IInvalidatedTagSource>().SelectMany(static source => source.InvalidatedTags))
            .Concat(ReferencedInvalidations.Read(AppContext.BaseDirectory))
            .Concat(alsoInvalidatedBy ?? [])
            .Select(TagPattern.Of).Distinct(StringComparer.Ordinal).ToArray();
        var elsewhere = MarkedElsewhere(services).Select(TagPattern.Of).Distinct(StringComparer.Ordinal).ToArray();
        var uninvalidated = new List<UninvalidatedTag>();
        foreach (var method in provider.GetRequiredService<ICacheAdministrator>().GetMethods().OrderBy(static m => m.MethodId, StringComparer.Ordinal))
        {
            foreach (var tag in method.Options.Tags)
            {
                var pattern = TagPattern.Of(tag);
                if (covered.Any(invalidated => TagPattern.Overlaps(pattern, invalidated))) continue;
                uninvalidated.Add(new UninvalidatedTag(tag, method.MethodId, elsewhere.Any(marked => TagPattern.Overlaps(pattern, marked))));
            }
        }
        return uninvalidated;
    }

    /// <summary>The methods of the services registered for caching, with those of the interfaces they inherit.</summary>
    private static MethodInfo[] RegisteredServices(IServiceProvider provider)
    {
        var registered = provider.GetService<MethodCacheServiceCollectionExtensions.RegistrationValidation>()?.Accepted ?? [];
        return registered.Select(static descriptor => descriptor.ServiceType).Distinct()
            .SelectMany(static service => service.GetMethods().Concat(service.GetInterfaces().SelectMany(static inherited => inherited.GetMethods())))
            .ToArray();
    }

    /// <summary>The tags of the <c>[CacheInvalidate]</c> methods.</summary>
    private static IEnumerable<string> InvalidatedBy(IEnumerable<MethodInfo> methods)
        => methods.SelectMany(static method => method.GetCustomAttribute<CacheInvalidateAttribute>()?.Tags ?? []);

    /// <summary>The <c>InvalidatedElsewhere</c> tags of the <c>[Cache]</c> methods.</summary>
    private static IEnumerable<string> MarkedElsewhere(IEnumerable<MethodInfo> methods)
        => methods.SelectMany(static method => method.GetCustomAttribute<CacheAttribute>()?.InvalidatedElsewhere ?? []);
}

/// <summary>A tag of a cached method that nothing invalidates, as <see cref="TagCoverage.Uninvalidated"/> reports it.</summary>
/// <param name="Tag">The tag, as written on the method or in its configuration, such as <c>"product:{id}"</c>.</param>
/// <param name="MethodId">The id of the cached method, as <see cref="CachedMethodInfo.MethodId"/> gives it.</param>
/// <param name="MarkedInvalidatedElsewhere">
/// The tag is in <c>InvalidatedElsewhere</c> on a <c>[Cache]</c>, but no project the tests reference invalidates it.
/// </param>
public sealed record UninvalidatedTag(string Tag, string MethodId, bool MarkedInvalidatedElsewhere)
{
    /// <summary>
    /// The tag and its method, such as <c>product:{id} on [MyApp]MyApp.IProductService.GetAsync</c>, and for a tag in
    /// <c>InvalidatedElsewhere</c> that no referenced project invalidates, a note that says so.
    /// </summary>
    public override string ToString()
        => MarkedInvalidatedElsewhere
            ? $"{Tag} on {MethodId} (marked InvalidatedElsewhere, but no project the tests reference invalidates it)"
            : $"{Tag} on {MethodId}";
}
