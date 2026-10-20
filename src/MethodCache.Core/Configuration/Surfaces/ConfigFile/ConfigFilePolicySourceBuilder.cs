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

    // Group, Metadata and Notes describe one method, so only a method's entry has them
    private static readonly HashSet<string> MethodOnlySettings = new(StringComparer.OrdinalIgnoreCase) { "Group", "Metadata", "Notes" };

    private static readonly HashSet<string> MethodSettings = new(GroupSettings.Concat(MethodOnlySettings), StringComparer.OrdinalIgnoreCase) { "MethodId" };

    private static readonly HashSet<string> CollectionSettings = new(StringComparer.OrdinalIgnoreCase) { "Tags", "Metadata" };

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
    /// silently ignored. With <paramref name="checks"/>, a method that isn't cached and a tag template that doesn't
    /// fit a cached method it reaches are rejected too.
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

            policies.Add(new ConfiguredPolicy(ParseSection(methodSection, MethodSettings).Build(methodId, methodSection["Notes"]), methodSection));
        }

        if (checks?.FindTagProblem(policies.Select(static policy => policy.Draft).ToArray()) is { } tagProblem)
        {
            throw TagError(root, policies, tagProblem);
        }

        return policies;
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

    /// <summary>Rejects Group, Metadata and Notes outside a method's entry, since they describe one method.</summary>
    private static void RejectMethodOnlySettings(IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            if (MethodOnlySettings.Contains(child.Key))
            {
                throw new InvalidOperationException(
                    $"MethodCache configuration '{child.Path}' can't be set here: {child.Key} describes one method, so set it on the " +
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

        if (section["Version"] is { } versionValue)
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
                $"MethodCache configuration '{failSafeSection.Path}' must be a time span such as \"01:00:00\" or \"off\". " +
                "Set FailSafeTimeout and FailSafeRetryAfter next to it; the FailSafe block with Duration, Timeout and RetryAfter belongs at the top of the section.");
        }

        if (section["FailSafe"] is { } failSafe)
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

        var groupValue = section["Group"];
        if (!string.IsNullOrWhiteSpace(groupValue))
        {
            builder.AddMetadata("group", groupValue);
        }

        // Metadata only describes the method; Group is the one setting that puts it in a group
        var metadataSection = section.GetSection("Metadata");
        if (metadataSection.Exists())
        {
            foreach (var child in metadataSection.GetChildren())
            {
                if (string.Equals(child.Key, "group", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"MethodCache configuration '{child.Path}' sets a group in Metadata, which only describes the method; " +
                        $"set '{section.Path}:Group' to put the method in a group.");
                }

                if (!string.IsNullOrWhiteSpace(child.Key))
                {
                    builder.AddMetadata(child.Key, child.Value);
                }
            }
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
            throw new InvalidOperationException($"MethodCache configuration '{section.Path}' needs a 'Duration', for example \"01:00:00\".");
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
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TimeSpan? ReadTimeSpan(IConfigurationSection section, string name, bool allowOff = false)
    {
        if (section[name] is not { } text)
        {
            return null;
        }

        // The same grammar is used by attributes and configuration.
        if (!MethodCache.Internal.DurationParser.TryParse(text, allowOff, out var value))
        {
            throw InvalidValue(section, name, text,
                allowOff ? "a positive time span such as \"00:05:00\", or \"off\"" : "a positive time span such as \"00:05:00\"");
        }

        return value;
    }

    private static void RejectUnknownSettings(IConfigurationSection section, HashSet<string> settings)
    {
        RejectSingleValue(section);
        foreach (var child in section.GetChildren())
        {
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
        if (CollectionSettings.Contains(setting.Key))
        {
            // A single value for Tags reports its own error
            if (string.Equals(setting.Key, "Metadata", StringComparison.OrdinalIgnoreCase))
            {
                RejectSingleValue(setting, "{ \"Owner\": \"team-a\" }");
            }

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
                $"MethodCache configuration '{setting.Path}' has nested settings; write it as a single value, such as \"00:05:00\" or \"users\".");
        }
    }

    /// <summary>Throws when a section that holds settings is written as one value, which would otherwise be ignored.</summary>
    private static void RejectSingleValue(IConfigurationSection section, string example = "{ \"Duration\": \"00:05:00\" }")
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
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                values.Add(child.Value);
            }
        }

        return values;
    }
}

/// <summary>A policy read from configuration, and the section it comes from, which errors name.</summary>
internal sealed record ConfiguredPolicy(PolicyDraft Draft, IConfigurationSection Section);
