#nullable enable
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // These settings turn an inherited feature off for one method with "off"
        private static bool AllowsOff(string setting) => setting is "FailSafe" or "SlidingExpiration" or "RefreshAhead";

        private static List<DiagnosticInfo> ValidateCacheAttribute(AttributeData attribute, Location? fallback)
        {
            var diagnostics = new List<DiagnosticInfo>();
            var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallback;
            foreach (var argument in attribute.NamedArguments)
            {
                switch (argument.Key)
                {
                    case "Duration":
                    case "SlidingExpiration":
                    case "RefreshAhead":
                    case "FailSafe":
                    case "FailSafeTimeout":
                    case "FailSafeRetryAfter":
                        if (argument.Value.Value is not string text || !MethodCache.Internal.DurationParser.TryParse(text, AllowsOff(argument.Key), out _))
                        {
                            // A TimeSpan such as "00:10:00" gets the same value with a unit
                            var suggestion = MethodCache.Internal.DurationParser.Suggest(argument.Value.Value as string);
                            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidDuration, location, argument.Key, argument.Value.Value?.ToString() ?? "null",
                                suggestion != null ? "\"" + suggestion + "\"" : MethodCache.Internal.DurationParser.Expected));
                        }
                        break;
                    case "Version":
                        if (argument.Value.Value is int version && version < 0)
                            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidVersion, location));
                        break;
                }
            }

            // A refresh window as long as the entry lives would reload the value on every hit
            if (TryGetDuration(attribute, "RefreshAhead", out var refreshText, out var refresh) && refresh > System.TimeSpan.Zero)
            {
                foreach (var lifetime in new[] { "Duration", "SlidingExpiration" })
                {
                    if (TryGetDuration(attribute, lifetime, out var lifetimeText, out var value) && value > System.TimeSpan.Zero && refresh >= value)
                    {
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RefreshAheadNotShorterThanDuration, location, refreshText, lifetime, lifetimeText));
                        break;
                    }
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// MC0038, a warning that leaves the method cached: a sliding window as long as the duration next to it, or
        /// longer. An entry never lives past its duration, so reads can never extend it.
        /// </summary>
        private static DiagnosticInfo? SlidingNotShorterThanDuration(AttributeData attribute, Location? fallback)
        {
            if (!TryGetDuration(attribute, "SlidingExpiration", out var slidingText, out var sliding) || sliding <= System.TimeSpan.Zero) return null;
            if (!TryGetDuration(attribute, "Duration", out var durationText, out var duration) || sliding < duration) return null;
            var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallback;
            return DiagnosticInfo.Create(Diagnostics.SlidingNotShorterThanDuration, location, slidingText, durationText);
        }

        private static bool TryGetDuration(AttributeData attribute, string setting, out string text, out System.TimeSpan value)
        {
            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Key == setting && argument.Value.Value is string candidate &&
                    MethodCache.Internal.DurationParser.TryParse(candidate, AllowsOff(setting), out value))
                {
                    text = candidate;
                    return true;
                }
            }

            text = string.Empty;
            value = default;
            return false;
        }
    }
}
