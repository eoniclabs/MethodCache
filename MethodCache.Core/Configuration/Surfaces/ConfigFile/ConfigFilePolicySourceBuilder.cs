using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Core.Configuration.Policies;
using Microsoft.Extensions.Configuration;

namespace MethodCache.Core.Configuration.Sources;

internal static class ConfigFilePolicySourceBuilder
{
    public static ConfigFilePolicySource FromConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        var root = configuration.GetSection(sectionName ?? string.Empty);

        if (!root.Exists())
        {
            return new ConfigFilePolicySource(Array.Empty<PolicyDraft>());
        }

        var defaults = ParseSection(root.GetSection("Defaults"));
        var servicesSection = root.GetSection("Services");

        if (!servicesSection.Exists())
        {
            return new ConfigFilePolicySource(Array.Empty<PolicyDraft>());
        }

        var descriptors = new List<PolicyDraft>();

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

            var notes = methodSection["Notes"] ?? methodSection["notes"];
            var baseBuilder = new CachePolicyBuilder();

            if (defaults.HasChanges)
            {
                baseBuilder.Apply(defaults, overwriteExisting: false);
            }

            var specific = ParseSection(methodSection);
            if (specific.HasChanges)
            {
                baseBuilder.Apply(specific, overwriteExisting: true);
            }

            var draft = baseBuilder.Build(methodId, notes);
            descriptors.Add(draft);
        }

        return new ConfigFilePolicySource(descriptors);
    }

    private static CachePolicyBuilder ParseSection(IConfigurationSection section)
    {
        var builder = new CachePolicyBuilder();

        if (!section.Exists())
        {
            return builder;
        }

        var durationValue = section["Duration"] ?? section["duration"];
        if (!string.IsNullOrWhiteSpace(durationValue) && TimeSpan.TryParse(durationValue, out var duration))
        {
            builder.WithDuration(duration);
        }

        var tags = ReadValues(section.GetSection("Tags")) ?? ReadValues(section.GetSection("tags"));
        if (tags is { Count: > 0 })
        {
            builder.SetTags(tags);
        }

        var versionValue = section["Version"] ?? section["version"];
        if (!string.IsNullOrWhiteSpace(versionValue) && int.TryParse(versionValue, out var version))
        {
            builder.WithVersion(version);
        }

        var keyGeneratorValue = section["KeyGenerator"] ?? section["keyGenerator"];
        if (!string.IsNullOrWhiteSpace(keyGeneratorValue))
        {
            var type = Type.GetType(keyGeneratorValue, throwOnError: false);
            if (type != null)
            {
                builder.WithKeyGenerator(type);
            }
        }

        var requireIdempotentValue = section["RequireIdempotent"] ?? section["requireIdempotent"];
        if (!string.IsNullOrWhiteSpace(requireIdempotentValue) && bool.TryParse(requireIdempotentValue, out var requireIdempotent))
        {
            builder.RequireIdempotent(requireIdempotent);
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
