using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.Internal
{
    // Compiled into the analyzer and the generator: the generator marks a project's assembly when it has cached services,
    // and marks each of its methods that calls AddMethodCache, and MC0030 in a program that references it reads the marks.
    internal static class HostMarkers
    {
        /// <summary><c>[assembly: AssemblyMetadata(CachedServices, "true")]</c>: the project has generated cached services.</summary>
        internal const string CachedServices = "MethodCache.CachedServices";

        /// <summary>
        /// <c>[assembly: AssemblyMetadata(AddsMethodCache, "M:...")]</c>, once per method: the method, named by its
        /// documentation id, calls AddMethodCache, directly or through other methods.
        /// </summary>
        internal const string AddsMethodCache = "MethodCache.AddsMethodCache";

        // Setup methods are named this way, so a call through one of them is followed to AddMethodCache
        private static readonly string[] SetupPrefixes = { "Add", "Use", "Configure", "Register" };

        /// <summary>
        /// A call that may reach AddMethodCache, told from its syntax alone: AddMethodCache itself, or a setup method such
        /// as <c>AddSpiderAdsMethodCache</c> that may call it.
        /// </summary>
        public static bool IsCandidate(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax invocation) return false;
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => null,
            };
            if (name == null) return false;
            foreach (var prefix in SetupPrefixes)
            {
                if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(name[prefix.Length])) return true;
            }

            return false;
        }

        /// <summary>
        /// For a call in a project, the method that makes it and what it calls: <see langword="null"/> as the callee when
        /// it calls AddMethodCache or a marked method of a referenced project, the callee's documentation id when it calls
        /// a method of its own project, which may call AddMethodCache. Nothing for any other call.
        /// </summary>
        public static (string Caller, string? Callee)? Edge(SemanticModel model, SyntaxNode invocation, CancellationToken cancellationToken)
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol target) return null;
            var definition = (target.ReducedFrom ?? target).OriginalDefinition;
            string? callee;
            if (IsAddMethodCache(target)) callee = null;
            else if (definition.GetDocumentationCommentId() is not { } id) return null;
            else if (SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, model.Compilation.Assembly)) callee = id;
            else if (Marked(definition.ContainingAssembly).Contains(id)) callee = null;
            else return null;

            var caller = model.GetEnclosingSymbol(invocation.SpanStart, cancellationToken);
            while (caller is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction }) caller = caller.ContainingSymbol;
            return caller?.GetDocumentationCommentId() is { } callerId ? (callerId, callee) : null;
        }

        /// <summary>The methods that call AddMethodCache: those calling it, or a marked method, directly, and their callers.</summary>
        public static IReadOnlyList<string> MarkedMethods(IEnumerable<(string Caller, string? Callee)> edges)
        {
            var all = edges.ToList();
            var marked = new HashSet<string>(all.Where(e => e.Callee == null).Select(e => e.Caller), StringComparer.Ordinal);
            for (var changed = marked.Count > 0; changed;)
            {
                changed = false;
                foreach (var (caller, callee) in all)
                {
                    if (callee != null && marked.Contains(callee) && marked.Add(caller)) changed = true;
                }
            }

            return marked.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        /// <summary>MethodCache's <c>services.AddMethodCache(...)</c>, not another library's method of that name.</summary>
        public static bool IsAddMethodCache(ISymbol? symbol)
            => symbol is IMethodSymbol { Name: "AddMethodCache" } method
               && (method.ReducedFrom ?? method).ContainingType is
               {
                   Name: "MethodCacheServiceCollectionExtensions",
                   ContainingNamespace: { Name: "DependencyInjection", ContainingNamespace: { Name: "Extensions", ContainingNamespace: { Name: "Microsoft" } } },
               } type
               && type.ContainingAssembly?.Name == "MethodCache.Core";

        /// <summary>Whether a referenced assembly carries the mark.</summary>
        public static bool Has(IAssemblySymbol assembly, string key)
            => assembly.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: "AssemblyMetadataAttribute" }
                && attribute.ConstructorArguments.Length == 2
                && attribute.ConstructorArguments[0].Value as string == key);

        /// <summary>The documentation ids of a referenced assembly's methods that call AddMethodCache.</summary>
        public static HashSet<string> Marked(IAssemblySymbol? assembly)
            => new(assembly == null ? Enumerable.Empty<string>() : assembly.GetAttributes()
                .Where(attribute => attribute.AttributeClass is { Name: "AssemblyMetadataAttribute" }
                    && attribute.ConstructorArguments.Length == 2
                    && attribute.ConstructorArguments[0].Value as string == AddsMethodCache)
                .Select(attribute => attribute.ConstructorArguments[1].Value as string)
                .OfType<string>(), StringComparer.Ordinal);
    }
}
