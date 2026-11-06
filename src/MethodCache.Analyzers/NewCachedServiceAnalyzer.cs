using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0032: a class that implements a cached interface is created with <c>new</c> outside a service
    /// registration. The cache is a decorator that dependency injection puts in front of the service, so the calls of an
    /// instance made with <c>new</c> never pass it and run uncached.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NewCachedServiceAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0032",
            "A cached service created with new isn't cached",
            "'{0}' implements the cached interface '{1}', so an instance created with new runs its [Cache] methods uncached. Get '{1}' from dependency injection, or register this instance before AddMethodCache, for example as a keyed service.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "MethodCache caches a service through a decorator that dependency injection puts in front of it when AddMethodCache runs. An instance created with new has no decorator, so its [Cache] methods run every time. Creating it inside a service registration, such as services.AddSingleton<IPrices>(sp => new Prices(...)), is fine, as AddMethodCache wraps what the registration returns. Not reported in test projects.",
            helpLinkUri: HelpLink("MC0032"));

        // Tests create implementations with new on purpose, to test them without the cache
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
                if (compilation.GetTypeByMetadataName("MethodCache.CacheAttribute") is null
                    || compilation.ReferencedAssemblyNames.Any(a => TestFrameworks.Contains(a.Name))) return;
                // Per class: the first cached interface it implements, or null
                var classes = new ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol?>(SymbolEqualityComparer.Default);
                start.RegisterOperationAction(operation => Analyze(operation, classes), OperationKind.ObjectCreation);
            });
        }

        private static void Analyze(OperationAnalysisContext context, ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol?> classes)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            if (creation.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class } type) return;
            var cached = classes.GetOrAdd(type.OriginalDefinition, static t => t.AllInterfaces.FirstOrDefault(HasCachedMethod));
            if (cached == null || InRegistration(creation)) return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Syntax.GetLocation(), type.Name, cached.Name));
        }

        /// <summary>
        /// Whether the instance goes to a service registration, as the instance or from the factory passed to
        /// <c>AddSingleton</c>, <c>AddKeyedScoped</c>, <c>TryAdd...</c> or a <c>ServiceDescriptor</c>, where AddMethodCache
        /// wraps it. An instance passed to another constructor or method first, such as <c>new Wrapper(new Prices())</c>,
        /// isn't the registered service.
        /// </summary>
        private static bool InRegistration(IOperation operation)
        {
            for (var current = operation.Parent; current != null; current = current.Parent)
            {
                if (current is not IArgumentOperation argument) continue;
                var target = argument.Parent switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod.ContainingType,
                    IObjectCreationOperation creation => creation.Type,
                    _ => null,
                };
                return target?.ContainingNamespace is { } ns && IsDependencyInjection(ns);
            }

            return false;
        }

        private static bool IsDependencyInjection(INamespaceSymbol ns)
        {
            // Microsoft.Extensions.DependencyInjection and its Extensions namespace, where TryAdd lives
            if (ns is { Name: "Extensions", ContainingNamespace.Name: "DependencyInjection" }) ns = ns.ContainingNamespace;
            return ns is { Name: "DependencyInjection", ContainingNamespace: { Name: "Extensions", ContainingNamespace: { Name: "Microsoft", ContainingNamespace.IsGlobalNamespace: true } } };
        }

        private static bool HasCachedMethod(INamedTypeSymbol @interface)
            => @interface.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }));
    }
}
