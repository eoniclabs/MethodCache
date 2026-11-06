using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using MethodCache.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0030: a program has cached services, its own or a referenced project's, but its code calls neither
    /// <c>AddMethodCache</c> nor a method of a referenced project that calls it. Without it nothing of MethodCache runs, so
    /// every <c>[Cache]</c> method runs uncached and nothing at run time can tell. A call in a referenced project counts
    /// only when the program calls the method it is in, since a shared setup method the program never calls sets
    /// up nothing.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MissingAddMethodCacheAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0030",
            "A program with cached services doesn't call AddMethodCache",
            "'{0}' has cached services in {1}, but its code calls neither AddMethodCache nor a method of a referenced project that calls it, so their [Cache] methods run uncached. Call services.AddMethodCache() after registering them, or your setup method that calls it. If this program doesn't use them, turn off MC0030 for it.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "AddMethodCache puts the cache in front of the services registered before it. A program that never calls it runs every [Cache] method uncached. Reported in a program (not a test project) whose own code has [Cache] interfaces, or that references a project that has, when its code calls neither AddMethodCache nor a method of a referenced project that calls it, directly or through other setup methods (Add..., Use..., Configure..., Register...).",
            helpLinkUri: HelpLink("MC0030"),
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        // A test project builds as a program too; its tests may use the implementations without the cache
        private static readonly string[] TestFrameworks =
        {
            "xunit.core", "xunit.v3.core", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework", "MSTest.TestFramework", "TUnit.Core",
        };

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var compilation = start.Compilation;
                if (compilation.Options.OutputKind is not (OutputKind.ConsoleApplication or OutputKind.WindowsApplication or OutputKind.WindowsRuntimeApplication)
                    || compilation.GetTypeByMetadataName("MethodCache.CacheAttribute") is null
                    || compilation.ReferencedAssemblyNames.Any(a => TestFrameworks.Contains(a.Name))) return;

                var found = new Found();
                // The referenced projects' methods that call AddMethodCache, read only for assemblies that mark any
                var marked = compilation.SourceModule.ReferencedAssemblySymbols
                    .Select(assembly => (assembly, methods: HostMarkers.Marked(assembly)))
                    .Where(pair => pair.methods.Count > 0)
                    .ToDictionary<(IAssemblySymbol assembly, HashSet<string> methods), IAssemblySymbol, HashSet<string>>(
                        pair => pair.assembly, pair => pair.methods, SymbolEqualityComparer.Default);
                start.RegisterSymbolAction(symbol =>
                {
                    if (!found.HasOwnServices && symbol.Symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } type && HasCachedMethod(type))
                        found.HasOwnServices = true;
                }, SymbolKind.NamedType);
                start.RegisterOperationAction(operation =>
                {
                    if (found.Calls) return;
                    var target = operation.Operation switch
                    {
                        IInvocationOperation invocation => invocation.TargetMethod,
                        IMethodReferenceOperation reference => reference.Method,
                        _ => null,
                    };
                    if (HostMarkers.IsAddMethodCache(target) || CallsMarked(target, marked)) found.Calls = true;
                }, OperationKind.Invocation, OperationKind.MethodReference);
                start.RegisterCompilationEndAction(end => Report(end, found));
            });
        }

        private sealed class Found
        {
            private int _hasOwnServices;
            private int _calls;

            public bool HasOwnServices
            {
                get => Volatile.Read(ref _hasOwnServices) == 1;
                set => Volatile.Write(ref _hasOwnServices, value ? 1 : 0);
            }

            public bool Calls
            {
                get => Volatile.Read(ref _calls) == 1;
                set => Volatile.Write(ref _calls, value ? 1 : 0);
            }
        }

        private static void Report(CompilationAnalysisContext context, Found found)
        {
            if (found.Calls) return;
            var compilation = context.Compilation;
            var referenced = compilation.SourceModule.ReferencedAssemblySymbols.Where(a => !IsFramework(a.Identity.Name)).ToList();

            var projects = new List<string>();
            if (found.HasOwnServices) projects.Add("its own code");
            projects.AddRange(referenced.Where(a => HostMarkers.Has(a, HostMarkers.CachedServices)).Select(a => a.Identity.Name).OrderBy(n => n, System.StringComparer.Ordinal));
            if (projects.Count == 0) return;

            var where = projects.Count <= 3 ? string.Join(", ", projects) : string.Join(", ", projects.Take(3)) + " and " + (projects.Count - 3) + " more";
            var entry = compilation.GetEntryPoint(context.CancellationToken)?.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(Rule, entry, compilation.AssemblyName, where));
        }

        private static bool CallsMarked(IMethodSymbol? target, Dictionary<IAssemblySymbol, HashSet<string>> marked)
        {
            if (target == null || marked.Count == 0) return false;
            var definition = (target.ReducedFrom ?? target).OriginalDefinition;
            return definition.ContainingAssembly is { } assembly && marked.TryGetValue(assembly, out var methods)
                && definition.GetDocumentationCommentId() is { } id && methods.Contains(id);
        }

        private static bool IsFramework(string name)
            => name is "mscorlib" or "netstandard" or "System" || name.StartsWith("System.", System.StringComparison.Ordinal)
               || name.StartsWith("Microsoft.", System.StringComparison.Ordinal);

        private static bool HasCachedMethod(INamedTypeSymbol @interface)
            => @interface.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }));
    }
}
