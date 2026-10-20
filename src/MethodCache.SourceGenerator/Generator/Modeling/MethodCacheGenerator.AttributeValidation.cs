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
                            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidDuration, location, argument.Key, argument.Value.Value?.ToString() ?? "null"));
                        break;
                    case "Version":
                        if (argument.Value.Value is int version && version < 0)
                            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidVersion, location));
                        break;
                }
            }

            // A refresh window as long as the entry lives would reload the value on every hit
            if (TryGetDuration(attribute, "Duration", out var durationText, out var duration) &&
                TryGetDuration(attribute, "RefreshAhead", out var refreshText, out var refresh) &&
                refresh > System.TimeSpan.Zero && refresh >= duration)
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RefreshAheadNotShorterThanDuration, location, refreshText, durationText));
            }

            return diagnostics;
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
