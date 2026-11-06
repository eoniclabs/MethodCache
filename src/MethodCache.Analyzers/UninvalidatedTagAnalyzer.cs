using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using MethodCache.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0028: a tag on a cached method that no invalidation in the project names, so its entries stay until
    /// they expire. The tags and the invalidations are read from the whole compilation and compared at its end, so an
    /// invalidation in one file covers a tag in another. A tag in <c>[Cache(InvalidatedElsewhere = ...)]</c> counts as
    /// invalidated; MC0029 reports one that matches none of the method's tags.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UninvalidatedTagAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0028",
            "Tag is never invalidated",
            "Nothing in this project invalidates tag \"{0}\" of '{1}', so its entries stay until they expire. Invalidate it where the data changes, or mark your own invalidation helper [InvalidatesCache]. When another application or process invalidates it, add it to InvalidatedElsewhere on [Cache].",
            "Reliability",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "A tag on a [Cache] method is there to be invalidated. The warning is reported when the project invalidates something, but nothing names this tag: no [CacheInvalidate] tag, EF Core mapping (Entity<T>, Tags, WhenDeleted), InvalidateTagAsync, InvalidateTagsAsync or InvalidateAfterCommit call, or call to an [InvalidatesCache] method with the tag as a constant, an interpolated string or IMethodCacheKeys.Tag. A tag in InvalidatedElsewhere on any [Cache] counts as invalidated. A [CacheInvalidate] tag on an interface of a referenced project counts too. A placeholder matches any value. Tags in configuration and other invalidations in other projects aren't seen.",
            helpLinkUri: HelpLink("MC0028"),
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        internal static readonly DiagnosticDescriptor UnknownElsewhereRule = new DiagnosticDescriptor(
            "MC0029",
            "InvalidatedElsewhere names a tag the method doesn't have",
            "\"{0}\" in InvalidatedElsewhere of '{1}' matches none of its Tags. Write the tag as it is written in Tags.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "InvalidatedElsewhere lists which of the method's tags another application or process invalidates. A tag that matches none of them, such as a misspelled one, leaves the real tag unmarked. A placeholder matches any value. Not reported when the attribute has no Tags, because the tags then come from configuration.",
            helpLinkUri: HelpLink("MC0029"));

        /// <summary>Stands for a placeholder or an interpolated value: any text.</summary>
        private const char Any = TagPattern.Any;

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule, UnknownElsewhereRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var found = new Found();
                start.RegisterSymbolAction(symbol => AnalyzeMethod(symbol, found), SymbolKind.Method);
                start.RegisterOperationAction(operation => AnalyzeInvocation((IInvocationOperation)operation.Operation, found), OperationKind.Invocation);
                start.RegisterCompilationEndAction(end => Report(end, found));
            });
        }

        /// <summary>The tags of cached methods and the invalidations, collected while the compilation is analyzed.</summary>
        private sealed class Found
        {
            public readonly ConcurrentBag<(string Template, Location Location, string Method)> Tags = new ConcurrentBag<(string, Location, string)>();
            public readonly ConcurrentBag<string> Patterns = new ConcurrentBag<string>();
            private int _invalidates;

            /// <summary>The project invalidates something, so it's where invalidations are written.</summary>
            public bool Invalidates => Volatile.Read(ref _invalidates) == 1;

            public void Invalidation(IEnumerable<string> patterns)
            {
                Volatile.Write(ref _invalidates, 1);
                foreach (var pattern in patterns) Patterns.Add(pattern);
            }

            /// <summary>Tags another application invalidates: covered, but not an invalidation in this project.</summary>
            public void Elsewhere(IEnumerable<string> patterns)
            {
                foreach (var pattern in patterns) Patterns.Add(pattern);
            }
        }

        private static void AnalyzeMethod(SymbolAnalysisContext context, Found found)
        {
            var method = (IMethodSymbol)context.Symbol;
            foreach (var attribute in method.GetAttributes())
            {
                if (attribute.AttributeClass is not { ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } } type) continue;
                if (type.Name == "CacheAttribute" && method.ContainingType?.TypeKind == TypeKind.Interface) AnalyzeElsewhere(context, attribute, method, found);
                var tags = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Tags").Value;
                if (tags.Kind != TypedConstantKind.Array || tags.IsNull) continue;
                var values = tags.Values.Select(v => v.Value as string).ToArray();

                if (type.Name == "CacheAttribute" && method.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    var elements = TagElements(attribute, "Tags", context.CancellationToken);
                    var name = method.ContainingType.Name + "." + method.Name;
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (values[i] is not { Length: > 0 } template) continue;
                        var location = i < elements.Count ? elements[i].GetLocation() : AttributeLocation(attribute, method);
                        found.Tags.Add((template, location, name));
                    }
                }
                else if (type.Name == "CacheInvalidateAttribute")
                {
                    found.Invalidation(values.Where(v => v != null).Select(v => TagPattern.Of(v!)));
                }
            }
        }

        /// <summary>Covers the tags in <c>InvalidatedElsewhere</c> and reports one that matches none of the method's tags.</summary>
        private static void AnalyzeElsewhere(SymbolAnalysisContext context, AttributeData attribute, IMethodSymbol method, Found found)
        {
            var elsewhere = Strings(attribute, "InvalidatedElsewhere");
            if (elsewhere.Count == 0) return;
            found.Elsewhere(elsewhere.Where(tag => tag != null).Select(tag => TagPattern.Of(tag!)));
            var tags = Strings(attribute, "Tags").Where(tag => tag != null).Select(tag => TagPattern.Of(tag!)).ToArray();
            // Without Tags on the attribute the method's tags come from configuration, which the analyzer can't read
            if (tags.Length == 0) return;
            var elements = TagElements(attribute, "InvalidatedElsewhere", context.CancellationToken);
            for (var i = 0; i < elsewhere.Count; i++)
            {
                if (elsewhere[i] is not { } tag) continue;
                var pattern = TagPattern.Of(tag);
                if (tags.Any(own => TagPattern.Overlaps(own, pattern))) continue;
                var location = i < elements.Count ? elements[i].GetLocation() : AttributeLocation(attribute, method);
                context.ReportDiagnostic(Diagnostic.Create(UnknownElsewhereRule, location, tag, method.ContainingType.Name + "." + method.Name));
            }
        }

        private static IReadOnlyList<string?> Strings(AttributeData attribute, string name)
        {
            var value = attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value;
            return value.Kind == TypedConstantKind.Array && !value.IsNull ? value.Values.Select(v => v.Value as string).ToArray() : [];
        }

        private static void AnalyzeInvocation(IInvocationOperation invocation, Found found)
        {
            var method = invocation.TargetMethod;
            if (method.Name is "InvalidateTagAsync" or "InvalidateTagsAsync" or "InvalidateAfterCommit")
            {
                if (HandBuiltTagAnalyzer.IsMethodCacheInvalidation(method)) found.Invalidation(Tags(invocation, template: false));
                return;
            }
            if (IsEntityMapping(method))
            {
                found.Invalidation(Tags(invocation, template: true));
                return;
            }

            // A call to a [CacheInvalidate] method invalidates its tags, also one of an interface in another project
            if (method.ContainingType?.TypeKind == TypeKind.Interface)
            {
                var invalidate = method.GetAttributes().FirstOrDefault(a => a.AttributeClass is
                    { Name: "CacheInvalidateAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } });
                var tags = invalidate?.NamedArguments.FirstOrDefault(a => a.Key == "Tags").Value;
                if (tags is { Kind: TypedConstantKind.Array, IsNull: false } array)
                {
                    found.Invalidation(array.Values.Select(v => v.Value as string).Where(v => v != null).Select(v => TagPattern.Of(v!)));
                    return;
                }
            }

            // A call to a helper of the app's own that invalidates the tags it is given. Its attribute is looked up only
            // for calls that pass a tag the analyzer can read.
            var given = Tags(invocation, template: false).ToList();
            if (given.Count > 0 && ChangeTrackerBypassAnalyzer.HasInvalidatesCache(method)) found.Invalidation(given);
        }

        /// <summary>ef.Entity&lt;T&gt;("...") and the builder's Tags("...") and WhenDeleted("...").</summary>
        private static bool IsEntityMapping(IMethodSymbol method)
        {
            var type = method.ContainingType?.OriginalDefinition;
            if (type?.ContainingNamespace is not { Name: "EntityFrameworkCore", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } })
                return false;
            return (type.Name == "EntityFrameworkCoreOptions" && method.Name == "Entity")
                || (type.Name == "EntityTagsBuilder" && method.Name is "Tags" or "WhenDeleted");
        }

        private static void Report(CompilationAnalysisContext context, Found found)
        {
            // A project that invalidates nothing, such as one that only declares the cached interfaces, leaves the
            // invalidation to another project
            if (!found.Invalidates || found.Tags.IsEmpty) return;
            var patterns = found.Patterns.Concat(ReferencedInvalidations(context.Compilation, context.CancellationToken)).Distinct().ToArray();
            foreach (var (template, location, method) in found.Tags)
            {
                var tag = TagPattern.Of(template);
                if (patterns.Any(pattern => TagPattern.Overlaps(tag, pattern))) continue;
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, template, method));
            }
        }

        /// <summary>The tags an invocation passes as constants, interpolated strings or <c>IMethodCacheKeys.Tag</c> calls.</summary>
        /// <summary>
        /// The tags of <c>[CacheInvalidate]</c> methods on the interfaces of referenced projects, such as a shared
        /// invalidation interface that another project calls. A declared invalidation counts as it does in the project.
        /// </summary>
        private static IEnumerable<string> ReferencedInvalidations(Compilation compilation, System.Threading.CancellationToken cancellationToken)
        {
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!assembly.Modules.Any(m => m.ReferencedAssemblies.Any(a => a.Name == "MethodCache.Core"))) continue;
                foreach (var type in Types(assembly.GlobalNamespace))
                {
                    if (type.TypeKind != TypeKind.Interface) continue;
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        var invalidate = method.GetAttributes().FirstOrDefault(a => a.AttributeClass is
                            { Name: "CacheInvalidateAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } });
                        var tags = invalidate?.NamedArguments.FirstOrDefault(a => a.Key == "Tags").Value;
                        if (tags is not { Kind: TypedConstantKind.Array, IsNull: false } array) continue;
                        foreach (var tag in array.Values.Select(v => v.Value as string).Where(v => v != null)) yield return TagPattern.Of(tag!);
                    }
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers()) yield return type;
            foreach (var child in ns.GetNamespaceMembers())
                foreach (var type in Types(child))
                    yield return type;
        }

        private static IEnumerable<string> Tags(IInvocationOperation invocation, bool template)
        {
            var tags = new List<string>();
            foreach (var argument in invocation.Arguments) Collect(argument.Value, template, tags);
            return tags;
        }

        private static void Collect(IOperation? value, bool template, List<string> tags)
        {
            while (value is IConversionOperation conversion) value = conversion.Operand;
            switch (value)
            {
                case null:
                    return;
                case { ConstantValue: { HasValue: true, Value: string text } }:
                    tags.Add(template ? TagPattern.Of(text) : text.Replace(Any, ' '));
                    return;
                case IInterpolatedStringOperation interpolated:
                    var pattern = new StringBuilder();
                    foreach (var part in interpolated.Parts)
                    {
                        if (part is IInterpolatedStringTextOperation { Text.ConstantValue: { HasValue: true, Value: string piece } }) pattern.Append(piece);
                        else pattern.Append(Any);
                    }
                    tags.Add(pattern.ToString());
                    return;
                case IInvocationOperation { TargetMethod.Name: "Tag" } call when IsKeys(call.TargetMethod.ContainingType):
                    var templateArgument = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "template")?.Value;
                    if (templateArgument?.ConstantValue is { HasValue: true, Value: string tagTemplate }) tags.Add(TagPattern.Of(tagTemplate));
                    return;
                case IArrayCreationOperation array when array.Initializer != null:
                    foreach (var element in array.Initializer.ElementValues) Collect(element, template, tags);
                    return;
                case { Syntax: CollectionExpressionSyntax }:
                    // Roslyn 4.8, which the analyzer is built against, has no operation type for a collection expression
                    foreach (var element in value.ChildOperations) Collect(element, template, tags);
                    return;
            }
        }

        private static bool IsKeys(INamedTypeSymbol? type)
        {
            if (type == null) return false;
            if (IsMethodCacheType(type, "IMethodCacheKeys")) return true;
            return type.AllInterfaces.Any(i => IsMethodCacheType(i, "IMethodCacheKeys"));
        }

        private static bool IsMethodCacheType(INamedTypeSymbol type, string name)
            => type.Name == name && type.ContainingNamespace is { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true };

        /// <summary>The tag expressions in <c>Tags = [...]</c> or another named array, in the order of the attribute's values.</summary>
        private static IReadOnlyList<SyntaxNode> TagElements(AttributeData attribute, string name, CancellationToken cancellationToken)
        {
            if (attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) is not AttributeSyntax syntax) return [];
            var tags = syntax.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name)?.Expression;
            return tags switch
            {
                CollectionExpressionSyntax collection => collection.Elements.Cast<SyntaxNode>().ToList(),
                ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.Cast<SyntaxNode>().ToList(),
                ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions.Cast<SyntaxNode>().ToList(),
                _ => [],
            };
        }

        private static Location AttributeLocation(AttributeData attribute, ISymbol symbol)
            => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? symbol.Locations[0];
    }
}
