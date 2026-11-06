using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0026: the implementation of a cached interface takes per-request state, such as the current user, in
    /// its constructor, or takes a class that does, such as a <c>DbContext</c> that filters rows by the current user. A
    /// cached result is shared by every caller and may be loaded outside the request (a refresh or a fail-safe retry), so
    /// a result that depends on that state can reach another user.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RequestStateAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0026",
            "Cached implementation depends on per-request state",
            "'{0}' implements '{1}', which has cached methods, and takes '{2}' ({3}). A cached result is shared by every caller and can be loaded outside the request, so a result that depends on the current request or user can reach another user. Pass the user or tenant as a parameter of the cached method, or don't cache it.",
            "Security",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The cache key is built from the method's arguments only. State a service reads from the current request, such as HttpContext, the signed-in user or a per-request database connection with row-level security, is not part of the key, and refresh-ahead and fail-safe loads run in a new scope without a request. A class the implementation takes, or creates through a factory such as IDbContextFactory<T>, is checked two constructors down, and an interface is followed to the classes of the project that implement it: a DbContext whose constructor takes IHttpContextAccessor is reported too. The application can name its own request-state types, such as ICurrentUser, in the .editorconfig option methodcache.request_state_types.",
            helpLinkUri: HelpLink("MC0026"));

        private static readonly string[] RequestStateTypes =
        {
            "Microsoft.AspNetCore.Http.IHttpContextAccessor",
            "Microsoft.AspNetCore.Http.HttpContext",
            "Microsoft.AspNetCore.Http.HttpRequest",
            "System.Security.Claims.ClaimsPrincipal",
            "System.Security.Principal.IPrincipal",
        };

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

        /// <summary>The .editorconfig option that names the application's own request-state types, comma-separated.</summary>
        internal const string RequestStateTypesOption = "methodcache.request_state_types";

        /// <summary>How many constructors down a dependency is followed: the one the implementation takes, and one more.</summary>
        private const int Depth = 2;

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                // The classes of this project that implement each interface, found once when a dependency is an interface
                var implementations = new Lazy<ILookup<ISymbol, INamedTypeSymbol>>(() => Implementations(start.Compilation));
                start.RegisterSymbolAction(symbol => AnalyzeType(symbol, implementations), SymbolKind.NamedType);
            });
        }

        private static void AnalyzeType(SymbolAnalysisContext context, Lazy<ILookup<ISymbol, INamedTypeSymbol>> implementations)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;

            var cached = type.AllInterfaces.FirstOrDefault(HasCachedMethod);
            if (cached is null) return;

            var search = new Search(Configured(context.Options, type), implementations);
            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor.IsImplicitlyDeclared || constructor.DeclaredAccessibility == Accessibility.Private) continue;
                foreach (var parameter in constructor.Parameters)
                {
                    var taken = Display(parameter.Type);
                    if (!search.IsRequestState(parameter.Type))
                    {
                        if (search.Dependency(parameter.Type, Depth, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)) is not var (dependency, state)) continue;
                        taken = SymbolEqualityComparer.Default.Equals(dependency, parameter.Type)
                            ? $"{taken}, whose constructor takes {Display(state)}"
                            : $"{taken}, and {Display(dependency)}'s constructor takes {Display(state)}";
                    }
                    context.ReportDiagnostic(Diagnostic.Create(Rule, parameter.Locations.FirstOrDefault() ?? type.Locations[0],
                        type.Name, Display(cached), parameter.Name, taken));
                }
            }
        }

        private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

        /// <summary>The request-state types the application names in .editorconfig, such as <c>MyApp.ICurrentUser</c>.</summary>
        private static ImmutableHashSet<string> Configured(AnalyzerOptions options, INamedTypeSymbol type)
        {
            var tree = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
            if (tree == null || !options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(RequestStateTypesOption, out var value))
                return ImmutableHashSet<string>.Empty;
            return value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim())
                .Where(name => name.Length > 0).ToImmutableHashSet(StringComparer.Ordinal);
        }

        private static ILookup<ISymbol, INamedTypeSymbol> Implementations(Compilation compilation)
            => Types(compilation.Assembly.GlobalNamespace)
                .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract)
                .SelectMany(type => type.AllInterfaces.Select(i => (Interface: (ISymbol)i.OriginalDefinition, Class: type)))
                .ToLookup(pair => pair.Interface, pair => pair.Class, SymbolEqualityComparer.Default);

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container)
        {
            foreach (var type in container.GetTypeMembers())
            {
                yield return type;
                foreach (var nested in Types(type)) yield return nested;
            }
            if (container is INamespaceSymbol ns)
                foreach (var child in ns.GetNamespaceMembers())
                    foreach (var type in Types(child))
                        yield return type;
        }

        private sealed class Search(ImmutableHashSet<string> configured, Lazy<ILookup<ISymbol, INamedTypeSymbol>> implementations)
        {
            /// <summary>
            /// A class the parameter's type is, implements (when it is an interface of this project), or names as a type
            /// argument (the context of <c>IDbContextFactory&lt;T&gt;</c>), whose constructor takes per-request state, followed
            /// <paramref name="depth"/> constructors down: past that the analyzer can't tell what runs.
            /// </summary>
            public (ITypeSymbol Dependency, ITypeSymbol State)? Dependency(ITypeSymbol type, int depth, HashSet<ITypeSymbol> seen)
            {
                var candidates = new List<ITypeSymbol> { type };
                if (type is INamedTypeSymbol { IsGenericType: true } generic) candidates.AddRange(generic.TypeArguments);
                foreach (var dependency in candidates.SelectMany(Classes))
                {
                    if (IsRequestState(dependency) || !seen.Add(dependency)) continue;
                    var constructors = dependency.InstanceConstructors
                        .Where(c => !c.IsImplicitlyDeclared && c.DeclaredAccessibility != Accessibility.Private).ToList();
                    var state = constructors.SelectMany(c => c.Parameters).FirstOrDefault(p => IsRequestState(p.Type));
                    if (state != null) return (dependency, state.Type);
                    if (depth <= 1) continue;
                    foreach (var parameter in constructors.SelectMany(c => c.Parameters))
                        if (Dependency(parameter.Type, depth - 1, seen) is { } deeper) return deeper;
                }
                return null;
            }

            private IEnumerable<INamedTypeSymbol> Classes(ITypeSymbol type)
                => type switch
                {
                    INamedTypeSymbol { TypeKind: TypeKind.Class } named => new[] { named },
                    INamedTypeSymbol { TypeKind: TypeKind.Interface } named => implementations.Value[named.OriginalDefinition],
                    _ => Enumerable.Empty<INamedTypeSymbol>(),
                };

            public bool IsRequestState(ITypeSymbol type)
            {
                bool Named(ITypeSymbol t) => RequestStateTypes.Contains(t.OriginalDefinition.ToDisplayString()) || configured.Contains(t.OriginalDefinition.ToDisplayString());
                for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
                {
                    if (Named(current)) return true;
                }
                return Named(type) || type.AllInterfaces.Any(Named);
            }
        }

        private static bool HasCachedMethod(INamedTypeSymbol @interface)
            => @interface.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }));
    }
}
