using MethodCache.Abstractions.Policies;
using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Policies;
using Microsoft.Extensions.Configuration;

namespace MethodCache.Core.Configuration.Sources;

internal static class ConfigFilePolicySourceBuilder
{
    internal readonly record struct PolicyDescriptor(
        string MethodId,
        CachePolicy Policy,
        CachePolicyFields Fields,
        IReadOnlyDictionary<string, string?>? Metadata,
        string? Notes);

    public static ConfigFilePolicySource FromConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        var root = configuration.GetSection(sectionName ?? string.Empty);

        if (!root.Exists())
        {
            return new ConfigFilePolicySource(Array.Empty<PolicyDescriptor>());
        }

        var defaults = ParseSettings(root.GetSection("Defaults"));
        var servicesSection = root.GetSection("Services");

        if (!servicesSection.Exists())
        {
            return new ConfigFilePolicySource(Array.Empty<PolicyDescriptor>());
        }

        var descriptors = new List<PolicyDescriptor>();

        foreach (var methodSection in servicesSection.GetChildren())
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

            var specific = ParseSettings(methodSection);
            var merged = MergeSettings(defaults, specific);
            var (policy, fields) = CachePolicyMapper.FromSettings(merged);
            var notes = methodSection["Notes"] ?? methodSection["notes"];

            descriptors.Add(new PolicyDescriptor(methodId, policy, fields, policy.Metadata, notes));
        }

        return new ConfigFilePolicySource(descriptors);
    }

    private static CacheMethodSettings ParseSettings(IConfigurationSection section)
    {
        var settings = new CacheMethodSettings();
        if (!section.Exists())
        {
            return settings;
        }

        var durationValue = section["Duration"] ?? section["duration"];
        if (!string.IsNullOrWhiteSpace(durationValue) && TimeSpan.TryParse(durationValue, out var duration))
        {
            settings.Duration = duration;
        }

        var tagsSection = section.GetSection("Tags");
        if (!tagsSection.Exists())
        {
            tagsSection = section.GetSection("tags");
        }

        if (tagsSection.Exists())
        {
            foreach (var child in tagsSection.GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(child.Value))
                {
                    settings.Tags.Add(child.Value);
                }
            }
        }

        var versionValue = section["Version"] ?? section["version"];
        if (!string.IsNullOrWhiteSpace(versionValue) && int.TryParse(versionValue, out var version))
        {
            settings.Version = version;
        }

        var keyGeneratorValue = section["KeyGenerator"] ?? section["keyGenerator"];
        if (!string.IsNullOrWhiteSpace(keyGeneratorValue))
        {
            var type = Type.GetType(keyGeneratorValue, throwOnError: false);
            if (type != null)
            {
                settings.KeyGeneratorType = type;
            }
        }

        var requireIdempotentValue = section["RequireIdempotent"] ?? section["requireIdempotent"];
        if (!string.IsNullOrWhiteSpace(requireIdempotentValue) && bool.TryParse(requireIdempotentValue, out var requireIdempotent))
        {
            settings.IsIdempotent = requireIdempotent;
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
                    settings.Metadata[child.Key] = child.Value;
                }
            }
        }

        return settings;
    }

    private static CacheMethodSettings MergeSettings(CacheMethodSettings defaults, CacheMethodSettings specifics)
    {
        if (defaults == null && specifics == null)
        {
            return new CacheMethodSettings();
        }

        if (defaults == null)
        {
            return specifics.Clone();
        }

        if (specifics == null)
        {
            return defaults.Clone();
        }

        var merged = defaults.Clone();

        if (specifics.Duration.HasValue)
        {
            merged.Duration = specifics.Duration;
        }

        if (specifics.Tags.Count > 0)
        {
            merged.Tags.Clear();
            merged.Tags.AddRange(specifics.Tags);
        }

        if (specifics.Version.HasValue)
        {
            merged.Version = specifics.Version;
        }

        if (specifics.KeyGeneratorType != null)
        {
            merged.KeyGeneratorType = specifics.KeyGeneratorType;
        }

        // Allow explicit false to override defaults by checking if defaults were true and specifics false.
        if (specifics.IsIdempotent || defaults.IsIdempotent && !specifics.IsIdempotent)
        {
            merged.IsIdempotent = specifics.IsIdempotent;
        }

        if (specifics.Metadata.Count > 0)
        {
            foreach (var kvp in specifics.Metadata)
            {
                merged.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return merged;
    }
}
