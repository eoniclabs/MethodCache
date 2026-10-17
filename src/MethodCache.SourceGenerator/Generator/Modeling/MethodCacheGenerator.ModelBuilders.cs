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
        private static (DiagnosticInfo? diagnostic, bool canProceed) ValidateCacheMethod(IMethodSymbol method, Location? reportAt = null)
        {
            var location = reportAt ?? method.Locations.FirstOrDefault();
            var methodName = method.ToDisplayString();

            // Check for void return
            if (method.ReturnsVoid)
            {
                return (DiagnosticInfo.Create(Diagnostics.UnsupportedVoidCache, location, methodName), false);
            }

            // Check for non-generic Task/ValueTask
            if (method.ReturnType is INamedTypeSymbol namedType)
            {
                var ns = namedType.ContainingNamespace?.ToDisplayString();
                if (ns == "System.Threading.Tasks" && !namedType.IsGenericType)
                {
                    if (namedType.Name == "Task" || namedType.Name == "ValueTask")
                    {
                        return (DiagnosticInfo.Create(Diagnostics.UnsupportedTaskCache, location, methodName), false);
                    }
                }
            }

            // A lazy sequence would be cached as the iterator or query, which runs again on every enumeration
            if (GetLazySequenceKind(method.ReturnType) is { } sequence)
            {
                return (DiagnosticInfo.Create(Diagnostics.UnsupportedLazySequence, location, methodName, sequence), false);
            }

            // References cannot be represented by a cached value or key.
            if (method.ReturnsByRef || method.ReturnsByRefReadonly || method.Parameters.Any(p => p.RefKind != RefKind.None))
            {
                return (DiagnosticInfo.Create(Diagnostics.UnsupportedRefParams, location, methodName), false);
            }

            // Check for pointer types
            if (IsPointerType(method.ReturnType) || method.Parameters.Any(p => IsPointerType(p.Type)))
            {
                return (DiagnosticInfo.Create(Diagnostics.UnsupportedPointerType, location, methodName), false);
            }

            // Check for ref struct types
            if (IsRefLikeType(method.ReturnType) || method.Parameters.Any(p => IsRefLikeType(p.Type)))
            {
                return (DiagnosticInfo.Create(Diagnostics.UnsupportedRefLikeType, location, methodName), false);
            }

            // Framework types must have a canonical key form.
            var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var parameter in method.Parameters)
            {
                if (!Utils.IsCancellationToken(parameter.Type) &&
                    FindTypeWithoutKeyForm(parameter.Type, parameter.Name, visited) is { } found)
                {
                    return (DiagnosticInfo.Create(Diagnostics.FrameworkTypeWithoutKeyForm, location, methodName, found.Path,
                        found.Type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)), false);
                }
            }

            // A distributed cache stores the declared type, so it can't bring back what object or an interface held
            var result = (Utils.IsTask(method.ReturnType, out var inner) || Utils.IsValueTask(method.ReturnType, out inner)) && inner != null
                ? inner
                : method.ReturnType;
            if (FindUnrestorableResultType(result) is { } unrestorable)
            {
                return (DiagnosticInfo.Create(Diagnostics.UnrestorableResultType, location, methodName,
                    result.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    unrestorable.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)), true);
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

        /// <summary>
        /// Whether the type is or contains a pointer or function pointer, such as <c>int*</c>, <c>int*[]</c> or
        /// <c>delegate*&lt;int&gt;[]</c>: code that names it must be in an unsafe context. C# allows no pointer as a
        /// type argument, so arrays are the only types that can hold one.
        /// </summary>
        private static bool IsPointerType(ITypeSymbol type)
            => type switch
            {
                { TypeKind: TypeKind.Pointer or TypeKind.FunctionPointer } => true,
                IArrayTypeSymbol array => IsPointerType(array.ElementType),
                _ => false,
            };

        private static bool IsRefLikeType(ITypeSymbol type)
        {
            if (type is ITypeParameterSymbol parameter)
            {
                return Utils.AllowsRefLikeType(parameter);
            }
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

        private static IParameterSymbol? ResolveTagParameter(IMethodSymbol method, string name)
        {
            var exact = method.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (exact != null) return exact;
            var matches = method.Parameters.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static List<(string template, List<string> paramNames)> ParseDynamicTags(List<string> tags, IMethodSymbol method)
        {
            var dynamicTags = new List<(string template, List<string> paramNames)>();

            foreach (var tag in tags)
            {
                var paramNames = new List<string>();
                var template = new System.Text.StringBuilder();
                var position = 0;

                // Find all {parameterName} patterns in the tag; everything else is literal text, with braces
                // doubled so string.Format keeps them
                foreach (System.Text.RegularExpressions.Match match in DynamicTagParameterRegex.Matches(tag))
                {
                    var parameter = ResolveTagParameter(method, match.Groups[1].Value);
                    if (parameter == null)
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

        private static List<DiagnosticInfo> ValidateDynamicTags(List<string> tags, IMethodSymbol method, Location? location = null)
        {
            var diagnostics = new List<DiagnosticInfo>();
            location ??= method.Locations.FirstOrDefault();

            foreach (var tag in tags)
            {
                var matches = DynamicTagParameterRegex.Matches(tag);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var paramName = match.Groups[1].Value;
                    if (ResolveTagParameter(method, paramName) == null)
                    {
                        diagnostics.Add(DiagnosticInfo.CreateWithParameter(
                            method.Parameters.Any(p => string.Equals(p.Name, paramName, StringComparison.OrdinalIgnoreCase))
                                ? Diagnostics.AmbiguousTagParameter : Diagnostics.DynamicTagParameterNotFound,
                            location,
                            // The code fix reads the unknown name from here to offer the closest parameter
                            paramName,
                            tag, paramName, method.ToDisplayString()));
                    }
                }

                // A property path or format string looks like a template but never matches one, so every call would
                // silently share the literal tag
                foreach (System.Text.RegularExpressions.Match match in MalformedTagPlaceholderRegex.Matches(tag))
                {
                    if (!DynamicTagParameterRegex.IsMatch(match.Value))
                    {
                        diagnostics.Add(DiagnosticInfo.Create(
                            Diagnostics.MalformedTagPlaceholder,
                            location,
                            tag, method.ToDisplayString(), match.Groups[1].Value));
                    }
                }
            }

            return diagnostics;
        }
    }
}
