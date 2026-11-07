using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MethodCache.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0026: the implementation of a cached interface takes per-request state, such as the current user, in
    /// its constructor, or takes a class that does, such as a <c>DbContext</c> that filters rows by the current user. A
    /// cached result is shared by every caller and may be loaded outside the request (a refresh or a fail-safe retry), so
    /// a result that depends on that state can reach another user. Also reports MC0036: a name in
    /// <c>methodcache.request_state_types</c> that matches no type.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RequestStateAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0026",
            "Cached implementation depends on per-request state",
            "'{0}' implements '{1}', which has cached methods, and takes '{2}' ({3}). A cached result is shared by every caller and can be loaded outside the request, so a result that depends on the current request or user can reach another user. Pass the user or tenant as a parameter of the cached method and check it against the request with a method marked [ChecksRequestState], or don't cache it.",
            "Security",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The cache key is built from the method's arguments only. State a service reads from the current request, such as HttpContext, the signed-in user or a per-request database connection with row-level security, is not part of the key, and refresh-ahead and fail-safe loads run in a new scope without a request. A class the implementation takes, or creates through a factory such as IDbContextFactory<T>, is checked two constructors down, and an interface is followed to the classes that implement it, in the project and in the projects it references: a DbContext whose constructor takes IHttpContextAccessor is reported too. The application can name its own request-state types, such as ICurrentUser, in the .editorconfig option methodcache.request_state_types. A class whose cached methods each pass one of their parameters to a method marked [ChecksRequestState] isn't reported.",
            helpLinkUri: HelpLink("MC0026"));

        internal static readonly DiagnosticDescriptor UnknownTypeRule = new DiagnosticDescriptor(
            "MC0036",
            "A name in methodcache.request_state_types matches no type",
            "\"{0}\" in methodcache.request_state_types matches no type, although namespace '{1}' exists, so MC0026 doesn't look for it. Write the type's full name with its namespace, as it is now, or remove the name.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The .editorconfig option names the application's own request-state types for MC0026. A name that matches no type, such as one left behind when the type was renamed or moved, turns the check for that type off without a sign. A name is checked only in a project that sees its namespace, so a solution-wide .editorconfig doesn't warn in projects that don't reference the type.",
            helpLinkUri: HelpLink("MC0036"),
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule, UnknownTypeRule);

        /// <summary>How many constructors down a dependency is followed: the one the implementation takes, and one more.</summary>
        private const int Depth = 2;

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var compilation = start.Compilation;
                // The classes of this project that implement each interface, and the classes of referenced projects that
                // take request state, found once when a dependency is an interface
                var implementations = new Lazy<ILookup<ISymbol, INamedTypeSymbol>>(() => RequestState.Implementations(compilation));
                var referenced = new Lazy<ILookup<string, RequestState.Taken>>(() => RequestState.Read(compilation));
                start.RegisterSymbolStartAction(symbol => AnalyzeType(symbol, implementations, referenced), SymbolKind.NamedType);
                start.RegisterCompilationEndAction(ReportUnknownTypes);
            });
        }

        private static void AnalyzeType(SymbolStartAnalysisContext context, Lazy<ILookup<ISymbol, INamedTypeSymbol>> implementations,
            Lazy<ILookup<string, RequestState.Taken>> referenced)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;

            var cachedInterfaces = type.AllInterfaces.Where(HasCachedMethod).ToList();
            if (cachedInterfaces.Count == 0) return;

            var search = new Search(Configured(context.Options, type), implementations, referenced);
            var found = new List<(IParameterSymbol Parameter, string Taken)>();
            foreach (var constructor in RequestState.Constructors(type))
            {
                foreach (var parameter in constructor.Parameters)
                {
                    var taken = RequestState.Display(parameter.Type);
                    if (!search.IsRequestState(parameter.Type))
                    {
                        if (search.Dependency(parameter.Type, Depth, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)) is not var (dependency, state)) continue;
                        taken = dependency == taken
                            ? $"{taken}, whose constructor takes {state}"
                            : $"{taken}, and {dependency}'s constructor takes {state}";
                    }
                    found.Add((parameter, taken));
                }
            }
            if (found.Count == 0) return;

            // A class whose cached methods each pass one of their parameters to a [ChecksRequestState] method loads a
            // result only for a request that matches its key
            var cachedMethods = CachedMethods(type, cachedInterfaces);
            var checkedMethods = new ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
            if (cachedMethods.Count > 0)
            {
                context.RegisterOperationAction(operation =>
                {
                    var invocation = (IInvocationOperation)operation.Operation;
                    if (!ChecksRequestState(invocation.TargetMethod)) return;
                    foreach (var argument in invocation.Arguments)
                    {
                        var value = argument.Value;
                        while (value is IConversionOperation conversion) value = conversion.Operand;
                        if (value is IParameterReferenceOperation { Parameter.ContainingSymbol: { } method } && cachedMethods.Contains(method))
                            checkedMethods[method] = true;
                    }
                }, OperationKind.Invocation);
            }

            context.RegisterSymbolEndAction(end =>
            {
                if (cachedMethods.Count > 0 && cachedMethods.All(checkedMethods.ContainsKey)) return;
                foreach (var (parameter, taken) in found)
                {
                    end.ReportDiagnostic(Diagnostic.Create(Rule, parameter.Locations.FirstOrDefault() ?? type.Locations[0],
                        type.Name, RequestState.Display(cachedInterfaces[0]), parameter.Name, taken));
                }
            });
        }

        /// <summary>The methods of the class that implement its cached interface methods.</summary>
        private static ImmutableHashSet<ISymbol> CachedMethods(INamedTypeSymbol type, IEnumerable<INamedTypeSymbol> cachedInterfaces)
            => cachedInterfaces
                .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>().Where(IsCached))
                .Select(member => type.FindImplementationForInterfaceMember(member))
                .Where(implementation => implementation != null)
                .ToImmutableHashSet(SymbolEqualityComparer.Default)!;

        /// <summary>A method marked [ChecksRequestState], itself or as the interface method it implements.</summary>
        private static bool ChecksRequestState(IMethodSymbol method)
        {
            method = method.ReducedFrom ?? method;
            if (HasChecksRequestState(method)) return true;
            if (method.ContainingType is not { TypeKind: TypeKind.Class } type) return false;
            for (var overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                if (HasChecksRequestState(overridden)) return true;
            return type.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
                .Any(member => HasChecksRequestState(member) && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method));
        }

        private static bool HasChecksRequestState(IMethodSymbol method)
            => method.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "ChecksRequestStateAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } });

        /// <summary>The request-state types the application names in .editorconfig, such as <c>MyApp.ICurrentUser</c>.</summary>
        private static ImmutableHashSet<string> Configured(AnalyzerOptions options, INamedTypeSymbol type)
        {
            var tree = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
            return tree != null && options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(RequestState.Option, out var value)
                ? RequestState.Configured(value)
                : ImmutableHashSet<string>.Empty;
        }

        /// <summary>
        /// Reports each configured name that matches no type in a namespace the project sees: a type that was renamed or
        /// moved. A name whose namespace the project doesn't see, such as a type of the web project in a solution-wide
        /// .editorconfig read by a library, isn't checked.
        /// </summary>
        private static void ReportUnknownTypes(CompilationAnalysisContext context)
        {
            var names = context.Compilation.SyntaxTrees
                .Select(tree => context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(RequestState.Option, out var value) ? value : null)
                .Where(value => value != null)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(value => RequestState.Configured(value))
                .Distinct(StringComparer.Ordinal);
            foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var simple = StripTypeArguments(name, out var arity);
                var dot = simple.LastIndexOf('.');
                var ns = dot < 0 ? "" : simple.Substring(0, dot);
                if (TypeExists(context.Compilation, simple, arity) || !NamespaceExists(context.Compilation, ns)) continue;
                context.ReportDiagnostic(Diagnostic.Create(UnknownTypeRule, Location.None, name, ns.Length == 0 ? "<global>" : ns));
            }
        }

        /// <summary><c>MyApp.IRepository&lt;T&gt;</c> as <c>MyApp.IRepository</c> with one type parameter.</summary>
        private static string StripTypeArguments(string name, out int arity)
        {
            arity = 0;
            var open = name.IndexOf('<');
            if (open < 0) return name;
            arity = name.Substring(open).Count(c => c == ',') + 1;
            return name.Substring(0, open);
        }

        /// <summary>A type by its display name, where a nested type is written <c>Outer.Inner</c>.</summary>
        private static bool TypeExists(Compilation compilation, string name, int arity)
        {
            var metadataName = arity == 0 ? name : name + "`" + arity;
            // A dot may stand for a nested type's '+': try each dot from the end
            for (var candidate = metadataName; ; )
            {
                if (!compilation.GetTypesByMetadataName(candidate).IsEmpty) return true;
                var dot = candidate.LastIndexOf('.');
                if (dot < 0) return false;
                candidate = candidate.Substring(0, dot) + "+" + candidate.Substring(dot + 1);
            }
        }

        private static bool NamespaceExists(Compilation compilation, string name)
        {
            INamespaceSymbol? ns = compilation.GlobalNamespace;
            foreach (var part in name.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ns = ns.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                if (ns == null) return false;
            }
            return true;
        }

        private sealed class Search(ImmutableHashSet<string> configured, Lazy<ILookup<ISymbol, INamedTypeSymbol>> implementations,
            Lazy<ILookup<string, RequestState.Taken>> referenced)
        {
            /// <summary>
            /// A class the parameter's type is, implements (when it is an interface of this project or, as its project
            /// wrote it, of a referenced one), or names as a type argument (the context of <c>IDbContextFactory&lt;T&gt;</c>),
            /// whose constructor takes per-request state, followed <paramref name="depth"/> constructors down: past that the
            /// analyzer can't tell what runs.
            /// </summary>
            public (string Dependency, string State)? Dependency(ITypeSymbol type, int depth, HashSet<ITypeSymbol> seen)
            {
                if (Referenced(type) is { } taken) return (taken.Via, taken.State);
                foreach (var dependency in RequestState.Classes(type, implementations.Value))
                {
                    if (IsRequestState(dependency) || !seen.Add(dependency)) continue;
                    var constructors = RequestState.Constructors(dependency).ToList();
                    var state = constructors.SelectMany(c => c.Parameters).FirstOrDefault(p => IsRequestState(p.Type));
                    if (state != null) return (RequestState.Display(dependency), RequestState.Display(state.Type));
                    if (depth <= 1) continue;
                    foreach (var parameter in constructors.SelectMany(c => c.Parameters))
                        if (Dependency(parameter.Type, depth - 1, seen) is { } deeper) return deeper;
                }
                return null;
            }

            /// <summary>A class of a referenced project that implements the interface and takes request state.</summary>
            private RequestState.Taken? Referenced(ITypeSymbol type)
            {
                var interfaces = new List<ITypeSymbol> { type };
                if (type is INamedTypeSymbol { IsGenericType: true } generic) interfaces.AddRange(generic.TypeArguments);
                foreach (var candidate in interfaces)
                {
                    if (candidate.TypeKind != TypeKind.Interface || EntityMappingMetadata.MetadataName(candidate.OriginalDefinition) is not { } name) continue;
                    if (referenced.Value[name].FirstOrDefault() is { } taken) return taken;
                }
                return null;
            }

            public bool IsRequestState(ITypeSymbol type) => RequestState.Is(type, configured);
        }

        private static bool IsCached(IMethodSymbol method)
            => method.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } });

        private static bool HasCachedMethod(INamedTypeSymbol @interface) => @interface.GetMembers().OfType<IMethodSymbol>().Any(IsCached);
    }
}
