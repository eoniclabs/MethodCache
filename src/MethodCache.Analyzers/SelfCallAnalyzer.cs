using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0031: a class calls one of its own cached methods. The cache is a decorator in front of the service, so a
    /// call from inside the class goes straight to the method and runs every time.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SelfCallAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0031",
            "A call to the class's own cached method skips the cache",
            "'{0}' calls its own cached method '{1}', so the call skips the cache and runs every time. The cache is in front of the service, so only calls through the interface from other classes use it. Move the method to a service of its own and call it through that service's interface.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "MethodCache caches a service by putting a generated decorator in front of it. A call the implementation makes to its own [Cache] method, with or without this, or through a cast of this to the interface, never passes the decorator, so it isn't cached.",
            helpLinkUri: HelpLink("MC0031"));

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName("MethodCache.CacheAttribute") is null) return;
                // Per class: its cached interface methods and the methods that implement them
                var classes = new ConcurrentDictionary<INamedTypeSymbol, ImmutableDictionary<IMethodSymbol, IMethodSymbol>>(SymbolEqualityComparer.Default);
                start.RegisterOperationAction(operation => AnalyzeInvocation(operation, classes), OperationKind.Invocation);
            });
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, ImmutableDictionary<IMethodSymbol, IMethodSymbol>> classes)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var instance = invocation.Instance;
            while (instance is IConversionOperation conversion) instance = conversion.Operand;
            if (instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) return;
            if (context.ContainingSymbol.ContainingType is not { TypeKind: TypeKind.Class } type) return;

            var cachedMethods = classes.GetOrAdd(type.OriginalDefinition, CachedMethods);
            if (cachedMethods.IsEmpty || !cachedMethods.TryGetValue(invocation.TargetMethod.OriginalDefinition, out var cached)) return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), type.Name, cached.ContainingType.Name + "." + cached.Name));
        }

        private static ImmutableDictionary<IMethodSymbol, IMethodSymbol> CachedMethods(INamedTypeSymbol type)
        {
            var methods = ImmutableDictionary.CreateBuilder<IMethodSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>()).Where(IsCached))
            {
                // A call through a cast of this to the interface, and a call to the class's own method
                methods[member.OriginalDefinition] = member.OriginalDefinition;
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                    methods[implementation.OriginalDefinition] = member.OriginalDefinition;
            }
            return methods.ToImmutable();
        }

        private static bool IsCached(IMethodSymbol method)
            => method.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } });
    }
}
