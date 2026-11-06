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
    /// Reports MC0030: a program has cached services, its own or a referenced project's, but neither the program nor any
    /// project it references calls <c>AddMethodCache</c>. Without it nothing of MethodCache runs, so every <c>[Cache]</c>
    /// method runs uncached and nothing at run time can tell.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MissingAddMethodCacheAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0030",
            "A program with cached services doesn't call AddMethodCache",
            "'{0}' has cached services in {1}, but neither it nor a project it references calls AddMethodCache, so their [Cache] methods run uncached. Call services.AddMethodCache() after registering them. If this program doesn't use them, turn off MC0030 for it.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "AddMethodCache puts the cache in front of the services registered before it. A program that never calls it runs every [Cache] method uncached. Reported in a program (not a test project) whose own code has [Cache] interfaces, or that references a project that has, when neither its code nor a referenced project calls AddMethodCache.",
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
                start.RegisterSymbolAction(symbol =>
                {
                    if (!found.HasOwnServices && symbol.Symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } type && HasCachedMethod(type))
                        found.HasOwnServices = true;
                }, SymbolKind.NamedType);
                start.RegisterOperationAction(operation =>
                {
                    if (!found.Calls && HostMarkers.IsAddMethodCache(((IInvocationOperation)operation.Operation).TargetMethod)) found.Calls = true;
                }, OperationKind.Invocation);
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
            if (referenced.Any(a => HostMarkers.Has(a, HostMarkers.CallsAddMethodCache))) return;

            var projects = new List<string>();
            if (found.HasOwnServices) projects.Add("its own code");
            projects.AddRange(referenced.Where(a => HostMarkers.Has(a, HostMarkers.CachedServices)).Select(a => a.Identity.Name).OrderBy(n => n, System.StringComparer.Ordinal));
            if (projects.Count == 0) return;

            var where = projects.Count <= 3 ? string.Join(", ", projects) : string.Join(", ", projects.Take(3)) + " and " + (projects.Count - 3) + " more";
            var entry = compilation.GetEntryPoint(context.CancellationToken)?.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(Rule, entry, compilation.AssemblyName, where));
        }

        private static bool IsFramework(string name)
            => name is "mscorlib" or "netstandard" or "System" || name.StartsWith("System.", System.StringComparison.Ordinal)
               || name.StartsWith("Microsoft.", System.StringComparison.Ordinal);

        private static bool HasCachedMethod(INamedTypeSymbol @interface)
            => @interface.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }));
    }
}
