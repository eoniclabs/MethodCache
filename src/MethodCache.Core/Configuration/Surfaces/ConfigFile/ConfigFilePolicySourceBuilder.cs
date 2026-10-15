using System.Globalization;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Sources;
using Microsoft.Extensions.Configuration;

namespace MethodCache.Core.Configuration.Surfaces.ConfigFile;

internal static class ConfigFilePolicySourceBuilder
{
    // The setting names of a method or the Defaults section; configuration keys are case-insensitive
    private static readonly HashSet<string> PolicySettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Duration", "SlidingExpiration", "RefreshAhead", "FailSafe", "FailSafeTimeout", "FailSafeRetryAfter",
        "Tags", "Version", "Group", "Metadata", "Notes", "Enabled", "Distributed"
    };

    private static readonly HashSet<string> MethodSettings = new(PolicySettings, StringComparer.OrdinalIgnoreCase) { "MethodId", "ClearTags" };

    private static readonly HashSet<string> CollectionSettings = new(StringComparer.OrdinalIgnoreCase) { "Tags", "Metadata" };

    private static readonly HashSet<string> SectionSettings = new(StringComparer.OrdinalIgnoreCase) { "Defaults", "FailSafe", "Services" };

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
    /// Reads the cache policies in <paramref name="sectionName"/>. Throws <see cref="InvalidOperationException"/>
    /// for values that are not valid, so a typo is reported instead of silently ignored.
    /// </summary>
    public static IReadOnlyList<PolicyDraft> Parse(IConfiguration configuration, string sectionName)
    {
        var root = configuration.GetSection(sectionName);
        if (!root.Exists())
        {
            return Array.Empty<PolicyDraft>();
        }

        RejectUnknownSettings(root, SectionSettings);
        var descriptors = new List<PolicyDraft>();

        // Defaults reach every cached method, including ones configured only with [Cache], whose own values win
        var defaults = ParseSection(root.GetSection("Defaults"), PolicySettings);

        // "FailSafe": { "Duration", "Timeout", "RetryAfter" } turns fail-safe on for every cached method
        defaults.Apply(ParseFailSafeBlock(root.GetSection("FailSafe")), overwriteExisting: false);
        if (defaults.HasChanges)
        {
            descriptors.Add(defaults.Build(MethodIds.AllMethods));
        }

        var services = root.GetSection("Services");
        RejectSingleValue(services);
        foreach (var methodSection in services.GetChildren())
        {
            var methodId = methodSection["MethodId"];
            if (string.IsNullOrWhiteSpace(methodId))
            {
                methodId = methodSection.Key;
            }

            if (string.IsNullOrWhiteSpace(methodId))
            {
                continue;
            }

            var notes = methodSection["Notes"];
            descriptors.Add(ParseSection(methodSection, MethodSettings).Build(methodId, notes));
        }

        return descriptors;
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

        var sliding = ReadTimeSpan(section, "SlidingExpiration");
        if (sliding is { } slidingValue)
        {
            builder.WithSlidingExpiration(slidingValue);
        }

        if (ReadTimeSpan(section, "RefreshAhead") is { } refresh)
        {
            // A window as long as the entry lives would reload the value on every hit
            if (refresh >= duration || refresh >= sliding)
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

        var metadataSection = section.GetSection("Metadata");
        if (metadataSection.Exists())
        {
            foreach (var child in metadataSection.GetChildren())
            {
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

    private static TimeSpan? ReadTimeSpan(IConfigurationSection section, string name)
    {
        if (section[name] is not { } text)
        {
            return null;
        }

        // The same grammar is used by attributes and configuration.
        if (!MethodCache.Internal.DurationParser.TryParse(text, false, out var value))
        {
            throw InvalidValue(section, name, text, "a positive time span such as \"00:05:00\"");
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
        => new($"MethodCache configuration '{section.Path}:{name}' has value '{value}', which is not {expected}.");

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
