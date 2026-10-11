using System.Globalization;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Sources;
using Microsoft.Extensions.Configuration;

namespace MethodCache.Core.Configuration.Surfaces.ConfigFile;

internal static class ConfigFilePolicySourceBuilder
{
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

        var descriptors = new List<PolicyDraft>();

        // Defaults reach every cached method, including ones configured only with [Cache], whose own values win
        var defaults = ParseSection(root.GetSection("Defaults"));

        // "FailSafe": { "Duration", "Timeout", "RetryAfter" } turns fail-safe on for every cached method
        defaults.Apply(ParseFailSafeBlock(root.GetSection("FailSafe")), overwriteExisting: false);
        if (defaults.HasChanges)
        {
            descriptors.Add(defaults.Build(MethodIds.AllMethods));
        }

        foreach (var methodSection in root.GetSection("Services").GetChildren())
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

            var notes = methodSection["Notes"] ?? methodSection["notes"];
            descriptors.Add(ParseSection(methodSection).Build(methodId, notes));
        }

        return descriptors;
    }

    private static CachePolicyBuilder ParseSection(IConfigurationSection section)
    {
        var builder = new CachePolicyBuilder();

        if (!section.Exists())
        {
            return builder;
        }

        if (ReadTimeSpan(section, "Duration") is { } duration)
        {
            builder.WithDuration(duration);
        }

        var tags = ReadValues(section.GetSection("Tags")) ?? ReadValues(section.GetSection("tags"));
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

        if (ReadTimeSpan(section, "SlidingExpiration") is { } sliding)
        {
            builder.WithSlidingExpiration(sliding);
        }

        if (ReadTimeSpan(section, "RefreshAhead") is { } refresh)
        {
            builder.WithRefreshAhead(refresh);
        }

        if (ReadValue(section, "FailSafe") is { } failSafe)
        {
            builder.WithFailSafe(string.Equals(failSafe, "off", StringComparison.OrdinalIgnoreCase)
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

        var groupValue = section["Group"] ?? section["group"];
        if (!string.IsNullOrWhiteSpace(groupValue))
        {
            builder.AddMetadata("group", groupValue);
        }

        var metadataSection = section.GetSection("Metadata");
        if (!metadataSection.Exists())
        {
            metadataSection = section.GetSection("metadata");
        }

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
        if (ReadValue(section, name) is not { } text)
        {
            return null;
        }

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) || value <= TimeSpan.Zero)
        {
            throw InvalidValue(section, name, text, "a positive time span such as \"00:05:00\"");
        }

        return value;
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
