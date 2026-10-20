using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.PolicyPipeline.Sources;

/// <summary>
/// What a source that reloads its policies, such as appsettings, checks them against before they replace its current
/// ones, so a load that names an unknown method or adds a tag template that doesn't fit is rejected as a whole.
/// </summary>
internal sealed class PolicyLoadChecks
{
    private readonly Func<IReadOnlyCollection<string>> _cachedMethods;
    private readonly Func<IReadOnlyCollection<PolicyDraft>, TagProblem?> _findTagProblem;

    public PolicyLoadChecks(Func<IReadOnlyCollection<string>> cachedMethods, Func<IReadOnlyCollection<PolicyDraft>, TagProblem?> findTagProblem)
    {
        _cachedMethods = cachedMethods ?? throw new ArgumentNullException(nameof(cachedMethods));
        _findTagProblem = findTagProblem ?? throw new ArgumentNullException(nameof(findTagProblem));
    }

    /// <summary>The ids of the cached methods, or none when they aren't known, so ids can't be checked.</summary>
    public IReadOnlyCollection<string> CachedMethods => _cachedMethods();

    /// <summary>
    /// The first tag template that would reach a cached method it doesn't fit if <paramref name="drafts"/> replaced
    /// the source's policies, or <see langword="null"/>.
    /// </summary>
    public TagProblem? FindTagProblem(IReadOnlyCollection<PolicyDraft> drafts) => _findTagProblem(drafts);
}

/// <summary>A source that checks its policies with the <see cref="PolicyLoadChecks"/> the resolver gives it.</summary>
internal interface IUsesPolicyLoadChecks
{
    /// <summary>Called before the source's policies are first read, and again if loading is retried.</summary>
    void UseChecks(PolicyLoadChecks checks);
}

/// <summary>A tag template that doesn't fit a cached method it reaches.</summary>
/// <param name="MethodId">The cached method the tag reaches.</param>
/// <param name="LayerId">The id the tag is set under: the method's own id, a group's defaults or the defaults for all methods.</param>
/// <param name="Tag">The tag template.</param>
/// <param name="Reason">Why it doesn't fit, naming the placeholder.</param>
/// <param name="ValueFreeReason">Why it doesn't fit, without any part of the tag, for a message a log may show.</param>
/// <param name="Where">Where the tag is set, such as <c>mc.Defaults(...)</c>.</param>
/// <param name="FromChecked">Whether the tag comes from the policies being checked rather than another source.</param>
internal sealed record TagProblem(string MethodId, string LayerId, string Tag, string Reason, string ValueFreeReason, string Where, bool FromChecked);
