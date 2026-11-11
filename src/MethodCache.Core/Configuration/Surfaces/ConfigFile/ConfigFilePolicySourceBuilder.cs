using System.Globalization;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Sources;
using Microsoft.Extensions.Configuration;

namespace MethodCache.Core.Configuration.Surfaces.ConfigFile;

internal static class ConfigFilePolicySourceBuilder
{
    // The settings of the Defaults section; configuration keys are case-insensitive
    private static readonly HashSet<string> DefaultsSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Duration", "SlidingExpiration", "RefreshAhead", "FailSafe", "FailSafeTimeout", "FailSafeRetryAfter",
        "Tags", "Version", "Enabled", "Distributed"
    };

    // A group's defaults, as mc.Group(...) sets them
    private static readonly HashSet<string> GroupSettings = new(DefaultsSettings, StringComparer.OrdinalIgnoreCase) { "ClearTags" };

    // Group puts one method in a group, so only a method's entry has it
    private static readonly HashSet<string> MethodOnlySettings = new(StringComparer.OrdinalIgnoreCase) { "Group" };

    private static readonly HashSet<string> MethodSettings = new(GroupSettings.Concat(MethodOnlySettings), StringComparer.OrdinalIgnoreCase) { "MethodId" };

    // What a runtime override sent over HTTP names: one method, a group or every method, and who made it and why
    private static readonly HashSet<string> OverrideTargets = new(StringComparer.OrdinalIgnoreCase) { "MethodId", "Group", "All" };
    private static readonly HashSet<string> OverrideNotes = new(StringComparer.OrdinalIgnoreCase) { "Owner", "Reason", "Ticket", "ExpiresAt" };
    private static readonly HashSet<string> OverrideSettings = new(GroupSettings.Concat(OverrideTargets).Concat(OverrideNotes), StringComparer.OrdinalIgnoreCase);

    // Settings that held notes about a method in earlier versions; a JSON comment holds them now
    private static readonly HashSet<string> NoteSettings = new(StringComparer.OrdinalIgnoreCase) { "Notes", "Metadata" };

    private static readonly HashSet<string> SectionSettings = new(StringComparer.OrdinalIgnoreCase) { "Defaults", "FailSafe", "Groups", "Methods" };

    // A Defaults setting and the FailSafe block setting that sets the same value
    private static readonly (string Defaults, string Block)[] FailSafePairs =
        [("FailSafe", "Duration"), ("FailSafeTimeout", "Timeout"), ("FailSafeRetryAfter", "RetryAfter")];

    private static readonly HashSet<string> FailSafeSettings = new(StringComparer.OrdinalIgnoreCase) { "Duration", "Timeout", "RetryAfter" };

    public static ConfigFilePolicySource FromConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        return new ConfigFilePolicySource(configuration, sectionName ?? string.Empty);
    }

    /// <summary>
    /// Reads the cache policies in <paramref name="sectionName"/>, each with the section it comes from. Throws
    /// <see cref="InvalidOperationException"/> for values that are not valid, so a typo is reported instead of
    /// silently ignored. With <paramref name="checks"/>, a method that isn't cached, a <c>Duration</c> longer than the
    /// cache keeps entries and a tag template that doesn't fit a cached method it reaches are rejected too.
    /// </summary>
    public static IReadOnlyList<ConfiguredPolicy> Parse(IConfiguration configuration, string sectionName, PolicyLoadChecks? checks = null)
    {
        var root = configuration.GetSection(sectionName);
        if (!root.Exists())
        {
            return Array.Empty<ConfiguredPolicy>();
        }

        RejectUnknownSettings(root, SectionSettings);
        var policies = new List<ConfiguredPolicy>();

        // Defaults reach every cached method, including ones configured only with [Cache], whose own values win
        var defaultsSection = root.GetSection("Defaults");
        RejectMethodOnlySettings(defaultsSection);
        var defaults = ParseSection(defaultsSection, DefaultsSettings);

        // "FailSafe": { "Duration", "Timeout", "RetryAfter" } turns fail-safe on for every cached method
        var failSafeSection = root.GetSection("FailSafe");
        RejectFailSafeSetTwice(defaultsSection, failSafeSection);
        defaults.Apply(ParseFailSafeBlock(failSafeSection), overwriteExisting: false);
        if (defaults.HasChanges)
        {
            policies.Add(new ConfiguredPolicy(defaults.Build(MethodIds.AllMethods), defaultsSection));
        }

        // "Groups": { "orders": { ... } } sets defaults for the methods in a group, as mc.Group("orders", ...) does
        var groups = root.GetSection("Groups");
        RejectSingleValue(groups);
        foreach (var groupSection in groups.GetChildren())
        {
            RejectMethodOnlySettings(groupSection);
            policies.Add(new ConfiguredPolicy(ParseSection(groupSection, GroupSettings).Build(MethodIds.Group(groupSection.Key)), groupSection));
        }

        var cachedMethods = checks?.CachedMethods ?? Array.Empty<string>();
        var methods = root.GetSection("Methods");
        RejectSingleValue(methods);
        var configured = new Dictionary<string, IConfigurationSection>(StringComparer.Ordinal);
        foreach (var methodSection in methods.GetChildren())
        {
            var idValue = methodSection["MethodId"];
            var methodId = string.IsNullOrWhiteSpace(idValue) ? methodSection.Key : idValue;
            if (string.IsNullOrWhiteSpace(methodId))
            {
                continue;
            }

            // Errors quote a MethodId value only through InvalidValue, which keeps it out of logs
            if (MethodIds.IsDefaults(methodId))
            {
                const string defaultsAdvice = "a method id; '*' and 'group:<name>' hold defaults, which are set under Defaults and Groups:<name>";
                throw string.IsNullOrWhiteSpace(idValue)
                    ? new InvalidOperationException($"MethodCache configuration '{methodSection.Path}' is not {defaultsAdvice}.")
                    : InvalidValue(methodSection, "MethodId", idValue, defaultsAdvice);
            }

            if (configured.TryGetValue(methodId, out var first))
            {
                throw new InvalidOperationException(
                    $"MethodCache configuration '{first.Path}' and '{methodSection.Path}' configure the same method; keep one of them.");
            }

            configured.Add(methodId, methodSection);
            if (cachedMethods.Count > 0 && !cachedMethods.Contains(methodId, StringComparer.Ordinal))
            {
                throw string.IsNullOrWhiteSpace(idValue)
                    ? new InvalidOperationException(
                        $"MethodCache configuration '{methodSection.Path}' configures a method that isn't cached. {MethodIds.NotCached(methodId, cachedMethods)}")
                    : InvalidValue(methodSection, "MethodId", idValue,
                        $"the id of a cached method{MethodIds.ClosestHint(methodId, cachedMethods)}. {MethodIds.CachedMethodAdvice}");
            }

            policies.Add(new ConfiguredPolicy(ParseSection(methodSection, MethodSettings).Build(methodId), methodSection));
        }

        if (checks?.MaxDuration is { } maxDuration &&
            policies.FirstOrDefault(policy => policy.Draft.Policy.Duration > maxDuration) is { } tooLong)
        {
            throw InvalidValue(tooLong.Section, "Duration", tooLong.Section["Duration"] ?? string.Empty,
                $"at most MethodCacheMemoryOptions.MaxExpiration ({maxDuration}), the longest MethodCache keeps an entry; " +
                "shorten the Duration, or raise MaxExpiration with mc.Memory(o => o.MaxExpiration = ...)");
        }

        if (checks?.MaxDuration is { } maxWindow &&
            policies.FirstOrDefault(policy => policy.Draft.Policy.SlidingExpiration > maxWindow) is { } tooWide)
        {
            throw InvalidValue(tooWide.Section, "SlidingExpiration", tooWide.Section["SlidingExpiration"] ?? string.Empty,
                $"at most MethodCacheMemoryOptions.MaxExpiration ({maxWindow}), the longest MethodCache keeps an entry, so reads " +
                "would never extend it; shorten the window, or raise MaxExpiration with mc.Memory(o => o.MaxExpiration = ...)");
        }

        if (checks?.FindTagProblem(policies.Select(static policy => policy.Draft).ToArray()) is { } tagProblem)
        {
            throw TagError(root, policies, tagProblem);
        }

        return policies;
    }

    /// <summary>
    /// Reads one runtime override written like a method's entry in appsettings, as the admin endpoints take it: the
    /// settings it changes, what it applies to (<c>MethodId</c>, <c>Group</c> or <c>"All": true</c>) and who made it and
    /// why (<c>Owner</c>, <c>Reason</c>, <c>Ticket</c>, <c>ExpiresAt</c>). Errors name the setting as a configuration
    /// file's errors do.
    /// </summary>
    public static RuntimeOverrideRequest ParseOverride(IConfigurationSection section)
    {
        RejectUnknownSettings(section, OverrideSettings);
        var methodId = ReadValue(section, "MethodId");
        var group = ReadValue(section, "Group");
        var all = false;
        if (ReadValue(section, "All") is { } allText && (!bool.TryParse(allText, out all) || !all))
        {
            throw InvalidValue(section, "All", allText, "true; name one method with MethodId or one group with Group instead");
        }

        if ((methodId != null ? 1 : 0) + (group != null ? 1 : 0) + (all ? 1 : 0) != 1)
        {
            throw new InvalidOperationException(
                $"MethodCache override '{section.Path}' must name what it applies to with exactly one of MethodId, Group or \"All\": true.");
        }

        DateTimeOffset? expiresAt = null;
        if (ReadValue(section, "ExpiresAt") is { } expiresText)
        {
            if (!DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                throw InvalidValue(section, "ExpiresAt", expiresText, "a date and time such as \"2026-11-12T08:00:00Z\"");
            }

            expiresAt = at;
        }

        var (owner, reason, ticket) = (ReadValue(section, "Owner"), ReadValue(section, "Reason"), ReadValue(section, "Ticket"));
        var metadata = owner == null && reason == null && ticket == null && expiresAt == null
            ? null
            : new RuntimeOverrideMetadata { Owner = owner, Reason = reason, Ticket = ticket, ExpiresAt = expiresAt };
        return new RuntimeOverrideRequest(methodId, group, all, ParseSection(section, OverrideSettings), metadata);
    }

    /// <summary>
    /// Explains a tag template that doesn't fit a cached method, naming the configuration that causes it. A rejected
    /// reload logs the reason, so it holds no part of the tag; InvalidValue's message quotes the whole value.
    /// </summary>
    private static InvalidOperationException TagError(IConfigurationSection root, List<ConfiguredPolicy> policies, TagProblem problem)
    {
        if (problem.FromChecked && policies.FirstOrDefault(policy => policy.Draft.MethodId == problem.LayerId) is { } tagged)
        {
            var tag = tagged.Section.GetSection("Tags").GetChildren().FirstOrDefault(item => item.Value == problem.Tag);
            return InvalidValue(tagged.Section, tag == null ? "Tags" : $"Tags:{tag.Key}", problem.Tag,
                $"a tag template that fits '{problem.MethodId}': {problem.ValueFreeReason}");
        }

        // The tag is set elsewhere, and this configuration puts the method in the group it is set for
        var section = policies.FirstOrDefault(policy => policy.Draft.MethodId == problem.MethodId)?.Section ?? root;
        return new InvalidOperationException(
            $"MethodCache configuration '{section.Path}' lets a tag set in {problem.Where} reach its method, but {problem.ValueFreeReason}.");
    }

    /// <summary>Rejects Group outside a method's entry, since it puts one method in a group.</summary>
    private static void RejectMethodOnlySettings(IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            if (MethodOnlySettings.Contains(child.Key))
            {
                throw new InvalidOperationException(
                    $"MethodCache configuration '{child.Path}' can't be set here: {child.Key} puts one method in a group, so set it on the " +
                    "method's entry under Methods. Defaults and Groups take the settings that change caching.");
            }
        }
    }

    /// <summary>Rejects a fail-safe value set both in Defaults and in the FailSafe block, which would otherwise hide one.</summary>
    private static void RejectFailSafeSetTwice(IConfigurationSection defaults, IConfigurationSection block)
    {
        foreach (var (setting, blockSetting) in FailSafePairs)
        {
            if (defaults.GetSection(setting).Exists() && block.GetSection(blockSetting).Exists())
            {
                throw new InvalidOperationException(
                    $"MethodCache configuration sets the same fail-safe value in '{defaults.Path}:{setting}' and '{block.Path}:{blockSetting}'; keep one of them.");
            }
        }
    }

    private static CachePolicyBuilder ParseSection(IConfigurationSection section, HashSet<string> settings)
    {
        var builder = new CachePolicyBuilder();

        if (!section.Exists())
        {
            return builder;
        }

        RejectUnknownSettings(section, settings);

        foreach (var name in new[] { "Enabled", "Distributed" })
            if (ReadValue(section, name) is { } booleanText)
            {
                if (!bool.TryParse(booleanText, out var value)) throw InvalidValue(section, name, booleanText, "true or false");
                if (name == "Enabled") builder.WithEnabled(value);
                else builder.WithDistributed(value);
            }

        var duration = ReadTimeSpan(section, "Duration");
        if (duration is { } durationValue)
        {
            builder.WithDuration(durationValue);
        }

        var tagsSection = section.GetSection("Tags");
        if (!string.IsNullOrWhiteSpace(tagsSection.Value))
        {
            throw new InvalidOperationException(
                $"MethodCache configuration '{tagsSection.Path}' is a single value; write tags as a list, for example [\"users\"] (Tags__0 in environment variables).");
        }

        // "ClearTags": true makes the method's Tags replace those of its [Cache] attribute instead of adding to them
        if (ReadValue(section, "ClearTags") is { } clearTagsValue)
        {
            if (!bool.TryParse(clearTagsValue, out var clearTags))
            {
                throw InvalidValue(section, "ClearTags", clearTagsValue, "true or false");
            }

            if (clearTags)
            {
                builder.ClearTags();
            }
        }

        var tags = ReadValues(tagsSection);
        if (tags is { Count: > 0 })
        {
            builder.SetTags(tags);
        }

        if (ReadValue(section, "Version") is { } versionValue)
        {
            if (!int.TryParse(versionValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version < 0)
            {
                throw InvalidValue(section, "Version", versionValue, "a non-negative integer");
            }

            builder.WithVersion(version);
        }

        // "off" (zero) turns an inherited sliding expiration or refresh-ahead off
        var sliding = ReadTimeSpan(section, "SlidingExpiration", allowOff: true);
        if (sliding is { } slidingValue)
        {
            builder.WithSlidingExpiration(slidingValue);
        }

        if (ReadTimeSpan(section, "RefreshAhead", allowOff: true) is { } refresh)
        {
            // A window as long as the entry lives would reload the value on every hit
            if (refresh > TimeSpan.Zero && (refresh >= duration || (sliding > TimeSpan.Zero && refresh >= sliding)))
            {
                throw InvalidValue(section, "RefreshAhead", section["RefreshAhead"]!, "shorter than the section's Duration and SlidingExpiration");
            }

            builder.WithRefreshAhead(refresh);
        }

        var failSafeSection = section.GetSection("FailSafe");
        if (failSafeSection.GetChildren().Any())
        {
            throw new InvalidOperationException(
                $"MethodCache configuration '{failSafeSection.Path}' must be a duration such as \"1h\" or \"off\". " +
                "Set FailSafeTimeout and FailSafeRetryAfter next to it; the FailSafe block with Duration, Timeout and RetryAfter belongs at the top of the section.");
        }

        if (ReadValue(section, "FailSafe") is { } failSafe)
        {
            builder.WithFailSafe(string.Equals(failSafe.Trim(), "off", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.Zero
                : ReadTimeSpan(section, "FailSafe"));
        }

        if (ReadTimeSpan(section, "FailSafeTimeout") is { } failSafeTimeout)
        {
            builder.WithFailSafeTimeout(failSafeTimeout);
        }

        if (ReadTimeSpan(section, "FailSafeRetryAfter") is { } failSafeRetryAfter)
        {
            builder.WithFailSafeRetryAfter(failSafeRetryAfter);
        }

        if (settings == MethodSettings && ReadValue(section, "Group") is { } groupValue)
        {
            builder.AddMetadata("group", groupValue);
        }

        return builder;
    }

    private static CachePolicyBuilder ParseFailSafeBlock(IConfigurationSection section)
    {
        var builder = new CachePolicyBuilder();
        if (!section.Exists())
        {
            return builder;
        }

        RejectUnknownSettings(section, FailSafeSettings);

        if (ReadTimeSpan(section, "Duration") is not { } duration)
        {
            throw new InvalidOperationException($"MethodCache configuration '{section.Path}' needs a 'Duration', for example \"1h\".");
        }

        builder.WithFailSafe(duration);

        if (ReadTimeSpan(section, "Timeout") is { } timeout)
        {
            builder.WithFailSafeTimeout(timeout);
        }

        if (ReadTimeSpan(section, "RetryAfter") is { } retryAfter)
        {
            builder.WithFailSafeRetryAfter(retryAfter);
        }

        return builder;
    }

    private static string? ReadValue(IConfigurationSection section, string name)
    {
        // Configuration keys are case-insensitive, so "duration" matches too
        var value = section[name];
        return value == null ? null : NotBlank(section.GetSection(name), value);
    }

    /// <summary>
    /// Throws for a setting written without a value, such as <c>"Enabled": ""</c>, which would otherwise be read as
    /// unset and leave the setting from a lower layer in place without a word.
    /// </summary>
    private static string NotBlank(IConfigurationSection setting, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new InvalidOperationException(
            $"MethodCache configuration '{setting.Path}' is empty. Give it a value, or remove it to keep the value from the method's other settings.");
    }

    private static TimeSpan? ReadTimeSpan(IConfigurationSection section, string name, bool allowOff = false)
    {
        if (section[name] is not { } text)
        {
            return null;
        }

        NotBlank(section.GetSection(name), text);

        // The same grammar is used by attributes and configuration.
        if (!MethodCache.Internal.DurationParser.TryParse(text, allowOff, out var value))
        {
            var expected = "a duration: " + MethodCache.Internal.DurationParser.Expected + (allowOff ? ", or \"off\"" : "");
            // A TimeSpan such as "00:10:00" gets the same value with a unit. Only the full message holds it: the
            // short reason is logged, and never shows the value.
            var suggestion = MethodCache.Internal.DurationParser.Suggest(text) is { } units ? $" Write \"{units}\"." : "";
            throw ConfigurationValueError.Create($"{section.Path}:{name}", $"is not {expected}",
                $"MethodCache configuration '{section.Path}:{name}' has value '{text}', which is not {expected}.{suggestion}");
        }

        return value;
    }

    private static void RejectUnknownSettings(IConfigurationSection section, HashSet<string> settings)
    {
        RejectSingleValue(section);
        foreach (var child in section.GetChildren())
        {
            if (!settings.Contains(child.Key) && NoteSettings.Contains(child.Key) && settings != SectionSettings)
            {
                throw new InvalidOperationException(
                    $"MethodCache configuration '{child.Path}' is not a setting MethodCache reads: it doesn't keep notes about a " +
                    "method. Write them as a // comment next to the method's entry in the JSON file.");
            }

            if (!settings.Contains(child.Key))
            {
                throw new InvalidOperationException(
                    $"MethodCache configuration '{child.Path}' is not a setting MethodCache knows. " +
                    $"Use one of: {string.Join(", ", settings.OrderBy(static name => name, StringComparer.Ordinal))}.");
            }

            if (settings != SectionSettings)
            {
                RejectWrongShape(child);
            }
        }
    }

    /// <summary>
    /// Throws when a setting has a shape its parser would skip: Tags and Metadata hold single values, every other
    /// setting is one (a FailSafe object reports its own error).
    /// </summary>
    private static void RejectWrongShape(IConfigurationSection setting)
    {
        if (string.Equals(setting.Key, "Tags", StringComparison.OrdinalIgnoreCase))
        {
            // A single value for Tags reports its own error
            foreach (var item in setting.GetChildren())
            {
                RejectNested(item);
            }
        }
        else if (!string.Equals(setting.Key, "FailSafe", StringComparison.OrdinalIgnoreCase))
        {
            RejectNested(setting);
        }
    }

    private static void RejectNested(IConfigurationSection setting)
    {
        if (setting.GetChildren().Any())
        {
            throw new InvalidOperationException(
                $"MethodCache configuration '{setting.Path}' has nested settings; write it as a single value, such as \"5m\" or \"users\".");
        }
    }

    /// <summary>Throws when a section that holds settings is written as one value, which would otherwise be ignored.</summary>
    private static void RejectSingleValue(IConfigurationSection section, string example = "{ \"Duration\": \"5m\" }")
    {
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            throw new InvalidOperationException(
                $"MethodCache configuration '{section.Path}' is a single value; write it as an object, for example {example}.");
        }
    }

    private static InvalidOperationException InvalidValue(IConfigurationSection section, string name, string value, string expected)
        => ConfigurationValueError.Create($"{section.Path}:{name}", $"is not {expected}",
            $"MethodCache configuration '{section.Path}:{name}' has value '{value}', which is not {expected}.");

    private static List<string>? ReadValues(IConfigurationSection section)
    {
        if (section == null || !section.Exists())
        {
            return null;
        }

        var values = new List<string>();
        foreach (var child in section.GetChildren())
        {
            // An empty tag would match nothing, and a tag with nothing in it is a typo
            values.Add(NotBlank(child, child.Value ?? string.Empty));
        }

        return values;
    }
}

/// <summary>A runtime override read by <see cref="ConfigFilePolicySourceBuilder.ParseOverride"/>: one of a method, a group or every method, and the settings it changes.</summary>
internal sealed record RuntimeOverrideRequest(string? MethodId, string? Group, bool All, CachePolicyBuilder Policy, RuntimeOverrideMetadata? Metadata);

/// <summary>A policy read from configuration, and the section it comes from, which errors name.</summary>
internal sealed record ConfiguredPolicy(PolicyDraft Draft, IConfigurationSection Section);
