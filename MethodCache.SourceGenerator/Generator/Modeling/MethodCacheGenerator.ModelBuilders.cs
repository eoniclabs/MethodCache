#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator.Generator.Modeling
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Modeling Helpers ========================
        private static (Diagnostic? diagnostic, bool canProceed) ValidateCacheMethod(IMethodSymbol method)
        {
            var location = method.Locations.FirstOrDefault();
            var methodName = method.ToDisplayString();

            // Check for void return
            if (method.ReturnsVoid)
            {
                return (Diagnostic.Create(Diagnostics.MethodCacheGenerator.Diagnostics.UnsupportedVoidCache, location, methodName), false);
            }

            // Check for non-generic Task/ValueTask
            if (method.ReturnType is INamedTypeSymbol namedType)
            {
                var ns = namedType.ContainingNamespace?.ToDisplayString();
                if (ns == "System.Threading.Tasks" && !namedType.IsGenericType)
                {
                    if (namedType.Name == "Task" || namedType.Name == "ValueTask")
                    {
                        return (Diagnostic.Create(Diagnostics.MethodCacheGenerator.Diagnostics.UnsupportedTaskCache, location, methodName), false);
                    }
                }
            }

            // Check for ref/out/in parameters
            if (method.Parameters.Any(p => p.RefKind != RefKind.None))
            {
                return (Diagnostic.Create(Diagnostics.MethodCacheGenerator.Diagnostics.UnsupportedRefParams, location, methodName), false);
            }

            // Check for pointer types
            if (method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Pointer))
            {
                return (Diagnostic.Create(Diagnostics.MethodCacheGenerator.Diagnostics.UnsupportedPointerType, location, methodName), false);
            }

            // Check for ref struct types
            if (method.Parameters.Any(p => IsRefLikeType(p.Type)))
            {
                return (Diagnostic.Create(Diagnostics.MethodCacheGenerator.Diagnostics.UnsupportedRefLikeType, location, methodName), false);
            }

            return (null, true);
        }

        private static bool IsRefLikeType(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol namedType)
            {
                // Check for ref struct (Span<T>, ReadOnlySpan<T>, etc.)
                return namedType.IsRefLikeType;
            }

            return false;
        }

        private static List<string> ExtractTags(AttributeData attr)
        {
            var tags = new List<string>();
            var tagsArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "Tags");
            if (!tagsArg.Value.Values.IsDefaultOrEmpty)
            {
                foreach (var value in tagsArg.Value.Values)
                {
                    if (value.Value?.ToString() is string tag && !string.IsNullOrWhiteSpace(tag))
                    {
                        tags.Add(tag);
                    }
                }
            }

            return tags;
        }

        private static List<(string template, List<string> paramNames)> ParseDynamicTags(List<string> tags, IMethodSymbol method)
        {
            var dynamicTags = new List<(string template, List<string> paramNames)>();
            var parameterNames = method.Parameters.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var tag in tags)
            {
                var paramNames = new List<string>();
                var template = tag;

                // Find all {parameterName} patterns in the tag
                var matches = Infrastructure.MethodCacheGenerator.DynamicTagParameterRegex.Matches(tag);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var paramName = match.Groups[1].Value;
                    if (parameterNames.ContainsKey(paramName))
                    {
                        paramNames.Add(paramName);
                        // Replace {paramName} with {0}, {1}, etc. for string.Format
                        template = template.Replace(match.Value, $"{{{paramNames.Count - 1}}}");
                    }
                }

                if (paramNames.Any())
                {
                    dynamicTags.Add((template, paramNames));
                }
            }

            return dynamicTags;
        }

        private static List<Diagnostic> ValidateDynamicTags(List<string> tags, IMethodSymbol method)
        {
            var diagnostics = new List<Diagnostic>();
            var parameterNames = new HashSet<string>(method.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var tag in tags)
            {
                var matches = Infrastructure.MethodCacheGenerator.DynamicTagParameterRegex.Matches(tag);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var paramName = match.Groups[1].Value;
                    if (!parameterNames.Contains(paramName))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.MethodCacheGenerator.Diagnostics.DynamicTagParameterNotFound,
                            method.Locations.FirstOrDefault(),
                            tag, paramName, method.ToDisplayString()));
                    }
                }
            }

            return diagnostics;
        }
    }
}
