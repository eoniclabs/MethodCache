using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.PolicyPipeline.Resolution;

/// <summary>
/// Loads the cache policies at host startup (through <c>ValidateOnStart</c>), so invalid startup code or
/// appsettings, a policy for a method that isn't cached and a tag template that doesn't fit its method fail
/// <c>StartAsync</c> instead of the first cached call.
/// Without a generic host, the first cached call loads them and fails the same way. It also logs a warning for each
/// policy whose sliding window can never extend an entry or whose refresh window covers its whole duration, and for
/// each group that has settings but no cached method.
/// </summary>
internal sealed partial class PolicyStartupValidation
{
    internal sealed class Validator(IServiceProvider services) : IValidateOptions<PolicyStartupValidation>
    {
        public ValidateOptionsResult Validate(string? name, PolicyStartupValidation options)
        {
            services.GetService<MethodCacheServiceCollectionExtensions.RegistrationValidationState>()?.Validate();
            // Exceptions propagate unchanged, so startup fails with the same exception a first call would throw
            services.GetService<IOptions<MethodCacheMemoryOptions>>()?.Value.EnsureValid();
            var registry = services.GetRequiredService<PolicyRegistry>();
            // Runtime overrides that every instance applies are read before the host serves
            registry.StartSharedOverrides();
            if (services.GetService<ILoggerFactory>()?.CreateLogger("MethodCache.Policies") is { } logger)
            {
                foreach (var warning in Find(registry))
                {
#pragma warning disable CA2254 // The templates are the fixed ones below
                    logger.LogWarning(warning.Template, warning.Arguments);
#pragma warning restore CA2254
                }
            }
            return ValidateOptionsResult.Success;
        }
    }

    /// <summary>One startup warning: a fixed message template and its values, in the template's order.</summary>
    internal readonly partial record struct Warning(string Template, object?[] Arguments)
    {
        /// <summary>The message as the log shows it, with the values filled in.</summary>
        public override string ToString()
        {
            var next = 0;
            var arguments = Arguments;
            return Placeholder().Replace(Template, _ => Convert.ToString(arguments[next++], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        [System.Text.RegularExpressions.GeneratedRegex(@"\{[^{}]+\}")]
        private static partial System.Text.RegularExpressions.Regex Placeholder();
    }

    /// <summary>
    /// The warnings for the policies of <paramref name="services"/>, as startup logs them, for a test that asserts there
    /// are none.
    /// </summary>
    internal static IReadOnlyList<string> Warnings(IServiceProvider services)
        => Find(services.GetRequiredService<PolicyRegistry>()).Select(static warning => warning.ToString()).ToList();

    private static IEnumerable<Warning> Find(PolicyRegistry registry)
    {
        var policies = registry.GetAllPolicies();
        return SlidingWindows(policies).Concat(RefreshWindows(policies)).Concat(GroupsWithoutMethods(registry.Resolver.GroupsWithoutMethods()));
    }

    /// <summary>
    /// A warning for each policy whose sliding window is as long as its duration or longer. An entry never lives past
    /// its duration, so such a window never extends it. The two often come from different places, such as a method's
    /// sliding window and the duration of the defaults, which no build check sees together.
    /// </summary>
    internal static IEnumerable<Warning> SlidingWindows(IEnumerable<PolicyResolutionResult> policies)
    {
        foreach (var result in policies)
        {
            var policy = result.Policy;
            // DurationFor and SlidingFor decide per result, and a method that isn't cached has no entries to extend
            if (policy.Enabled == false || policy.DurationFor != null || policy.SlidingFor != null) continue;
            if (policy.SlidingExpiration is not { } sliding || sliding <= TimeSpan.Zero) continue;
            if (policy.Duration is not { } duration || duration <= TimeSpan.Zero || sliding < duration) continue;
            yield return new Warning(
                "The sliding window of {MethodId} ({SlidingExpiration}) is not shorter than its Duration ({Duration}), so reads never extend its entries: each one expires that long after it was loaded. The two can come from different places, such as SlidingExpiration on the method and Duration in Defaults. Use a shorter sliding window or a longer Duration, or turn sliding off for the method.",
                [result.MethodId, sliding, duration]);
        }
    }

    /// <summary>
    /// A warning for each policy whose refresh window is as long as its duration or longer. Such a window would refresh
    /// each entry as soon as it is stored, so the cache halves it instead. The two often come from different places,
    /// such as a method's refresh window and the duration of a runtime override.
    /// </summary>
    internal static IEnumerable<Warning> RefreshWindows(IEnumerable<PolicyResolutionResult> policies)
    {
        foreach (var result in policies)
        {
            var policy = result.Policy;
            if (policy.Enabled == false || policy.DurationFor != null) continue;
            if (policy.RefreshAhead is not { } refresh || refresh <= TimeSpan.Zero) continue;
            if (policy.Duration is not { } duration || duration <= TimeSpan.Zero || refresh < duration) continue;
            yield return new Warning(
                "The refresh window of {MethodId} ({RefreshAhead}) is not shorter than its Duration ({Duration}), so MethodCache refreshes its entries halfway through their Duration instead. The two can come from different places, such as RefreshAhead on the method and Duration in Defaults. Use a shorter RefreshAhead or a longer Duration.",
                [result.MethodId, refresh, duration]);
        }
    }

    /// <summary>
    /// A warning for each group that has settings but no cached method in it, which is usually a misspelt or
    /// differently cased group name. Configuration shared by several applications can hold groups another one uses, so
    /// it isn't an error.
    /// </summary>
    internal static IEnumerable<Warning> GroupsWithoutMethods(IEnumerable<(string Group, string Where, string? Closest)> groups)
    {
        foreach (var (group, where, closest) in groups)
        {
            yield return closest != null
                ? new Warning(
                    "The group '{Group}' has settings in {Where}, but no cached method in this application is in it, so they apply to nothing. Did you mean '{Closest}'? Group names are case-sensitive. If the settings are shared with an application whose methods are in the group, ignore this.",
                    [group, where, closest])
                : new Warning(
                    "The group '{Group}' has settings in {Where}, but no cached method in this application is in it, so they apply to nothing. Group names are case-sensitive. If the settings are shared with an application whose methods are in the group, ignore this.",
                    [group, where]);
        }
    }
}
