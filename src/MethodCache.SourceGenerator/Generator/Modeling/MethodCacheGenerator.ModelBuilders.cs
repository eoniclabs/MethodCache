#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Modeling Helpers ========================
        private static (Diagnostic? diagnostic, bool canProceed) ValidateCacheMethod(IMethodSymbol method, Location? reportAt = null)
        {
            var location = reportAt ?? method.Locations.FirstOrDefault();
            var methodName = method.ToDisplayString();

            // Check for void return
            if (method.ReturnsVoid)
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedVoidCache, location, methodName), false);
            }

            // Check for non-generic Task/ValueTask
            if (method.ReturnType is INamedTypeSymbol namedType)
            {
                var ns = namedType.ContainingNamespace?.ToDisplayString();
                if (ns == "System.Threading.Tasks" && !namedType.IsGenericType)
                {
                    if (namedType.Name == "Task" || namedType.Name == "ValueTask")
                    {
                        return (Diagnostic.Create(Diagnostics.UnsupportedTaskCache, location, methodName), false);
                    }
                }
            }

            // A lazy sequence would be cached as the iterator or query, which runs again on every enumeration
            if (GetLazySequenceKind(method.ReturnType) is { } sequence)
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedLazySequence, location, methodName, sequence), false);
            }

            // Check for ref/out/in parameters
            if (method.Parameters.Any(p => p.RefKind != RefKind.None))
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedRefParams, location, methodName), false);
            }

            // Check for pointer types
            if (method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Pointer))
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedPointerType, location, methodName), false);
            }

            // Check for ref struct types
            if (method.Parameters.Any(p => IsRefLikeType(p.Type)))
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedRefLikeType, location, methodName), false);
            }

            return (null, true);
        }

        /// <summary>
        /// Names the lazy sequence a method returns, directly or through Task/ValueTask: IEnumerable, IQueryable or
        /// IAsyncEnumerable. Task&lt;IEnumerable&lt;T&gt;&gt; is allowed, since async methods return materialized results.
        /// </summary>
        private static string? GetLazySequenceKind(ITypeSymbol returnType)
        {
            static string? Kind(ITypeSymbol type, bool viaTask)
            {
                if (type is not INamedTypeSymbol named)
                {
                    return null;
                }

                var name = named.OriginalDefinition.ToDisplayString();
                switch (name)
                {
                    case "System.Collections.Generic.IAsyncEnumerable<T>":
                        return "IAsyncEnumerable";
                    case "System.Linq.IQueryable<T>":
                    case "System.Linq.IQueryable":
                    case "System.Linq.IOrderedQueryable<T>":
                        return "IQueryable";
                    case "System.Collections.Generic.IEnumerable<T>":
                    case "System.Collections.IEnumerable":
                    case "System.Linq.IOrderedEnumerable<TElement>":
                        return viaTask ? null : "IEnumerable";
                    default:
                        return null;
                }
            }

            if ((Utils.IsTask(returnType, out var inner) || Utils.IsValueTask(returnType, out inner)) && inner != null)
            {
                return Kind(inner, viaTask: true);
            }

            return Kind(returnType, viaTask: false);
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
            // An attribute without Tags yields a default (non-array) constant, whose Values property throws
            if (tagsArg.Value.Kind == TypedConstantKind.Array && !tagsArg.Value.Values.IsDefaultOrEmpty)
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

        private static string EscapeFormatBraces(string text) => text.Replace("{", "{{").Replace("}", "}}");

        private static List<(string template, List<string> paramNames)> ParseDynamicTags(List<string> tags, IMethodSymbol method)
        {
            var dynamicTags = new List<(string template, List<string> paramNames)>();
            var parameterNames = method.Parameters.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var tag in tags)
            {
                var paramNames = new List<string>();
                var template = new System.Text.StringBuilder();
                var position = 0;

                // Find all {parameterName} patterns in the tag; everything else is literal text, with braces
                // doubled so string.Format keeps them
                foreach (System.Text.RegularExpressions.Match match in DynamicTagParameterRegex.Matches(tag))
                {
                    if (!parameterNames.TryGetValue(match.Groups[1].Value, out var parameter))
                    {
                        continue;
                    }

                    template.Append(EscapeFormatBraces(tag.Substring(position, match.Index - position)));
                    // Use the parameter's own spelling, since the template may differ in case
                    paramNames.Add(parameter.Name);
                    template.Append('{').Append(paramNames.Count - 1).Append('}');
                    position = match.Index + match.Length;
                }

                template.Append(EscapeFormatBraces(tag.Substring(position)));

                if (paramNames.Any())
                {
                    dynamicTags.Add((template.ToString(), paramNames));
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
                var matches = DynamicTagParameterRegex.Matches(tag);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var paramName = match.Groups[1].Value;
                    if (!parameterNames.Contains(paramName))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.DynamicTagParameterNotFound,
                            method.Locations.FirstOrDefault(),
                            // The code fix reads the unknown name from here to offer the closest parameter
                            System.Collections.Immutable.ImmutableDictionary<string, string?>.Empty.Add("Parameter", paramName),
                            tag, paramName, method.ToDisplayString()));
                    }
                }
            }

            return diagnostics;
        }
    }
}
