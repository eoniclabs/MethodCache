using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0035: the factory of <c>GetOrCreateAsync</c> or <c>GetOrCreateManyAsync</c> uses a cancellation token from
    /// outside it that the call doesn't pass as its <c>cancellationToken</c>. Callers of one key share one factory run;
    /// the cache knows the token of the caller that started it, and when that one is cancelled the others load again.
    /// Cancelling a token the cache doesn't know fails the load for every caller waiting on it.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class FactoryTokenAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0035",
            "A cache factory uses a cancellation token the call doesn't pass",
            "The factory uses '{0}', which the call doesn't pass as its cancellationToken, so when '{0}' is cancelled the load fails for every caller waiting on it, not just this one. Use the factory's own token and pass '{0}' as cancellationToken.",
            "Reliability",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Callers of one key share one run of the factory. The cache gives the factory the token of the caller that started it, and when that caller cancels, the others start the load again. A token from outside the factory that the call doesn't pass is one the cache doesn't know, so its cancellation fails the load for all of them, and a fail-safe value may be served instead.",
            helpLinkUri: HelpLink("MC0035"));

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
                if (compilation.GetTypeByMetadataName("MethodCache.ICacheManager") is not { } manager
                    || compilation.GetTypeByMetadataName("System.Threading.CancellationToken") is not { } token) return;
                start.RegisterOperationAction(operation => Analyze(operation, manager, token), OperationKind.Invocation);
            });
        }

        private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol manager, INamedTypeSymbol token)
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod.Name is not ("GetOrCreateAsync" or "GetOrCreateManyAsync") || !IsCacheManagerCall(invocation.TargetMethod, manager)) return;

            // The token the call passes, which the cache gives the factory: using it there changes nothing
            var passed = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "cancellationToken" && a.ArgumentKind == ArgumentKind.Explicit)?.Value;
            foreach (var argument in invocation.Arguments)
            {
                if (Unwrap(argument.Value) is not IDelegateCreationOperation { Target: IAnonymousFunctionOperation factory }
                    || !factory.Symbol.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, token))) continue;

                foreach (var used in factory.Body.Descendants())
                {
                    if (!SymbolEqualityComparer.Default.Equals(used.Type, token) || !IsFromOutside(used, factory) || SameToken(used, passed)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(Rule, used.Syntax.GetLocation(), used.Syntax.ToString()));
                }
            }
        }

        private static bool IsCacheManagerCall(IMethodSymbol method, INamedTypeSymbol manager)
        {
            var type = method.ContainingType;
            if (type is { Name: "CacheManagerExtensions", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }) return true;
            return SymbolEqualityComparer.Default.Equals(type, manager) || type.AllInterfaces.Contains(manager, SymbolEqualityComparer.Default);
        }

        private static IOperation? Unwrap(IOperation? operation)
        {
            while (operation is IConversionOperation conversion) operation = conversion.Operand;
            return operation;
        }

        /// <summary>
        /// Whether <paramref name="used"/> reads a token from outside <paramref name="factory"/>: a parameter or local of the
        /// method around it, a field, or a property of one of those, such as <c>HttpContext.RequestAborted</c>. A static
        /// property such as <c>CancellationToken.None</c> and a token made inside the factory don't count.
        /// </summary>
        private static bool IsFromOutside(IOperation used, IAnonymousFunctionOperation factory)
        {
            var span = factory.Syntax.Span;
            switch (used)
            {
                case IParameterReferenceOperation parameter:
                    return !parameter.Parameter.Locations.Any(l => span.Contains(l.SourceSpan));
                case ILocalReferenceOperation local:
                    return !local.Local.Locations.Any(l => span.Contains(l.SourceSpan));
                case IFieldReferenceOperation field:
                    return field.Instance == null || RootIsFromOutside(field.Instance, span);
                case IPropertyReferenceOperation { Property.IsStatic: false, Instance: { } instance }:
                    return RootIsFromOutside(instance, span);
                default:
                    return false;
            }
        }

        // The value a member is read from: this, or a parameter, local or field from outside the factory
        private static bool RootIsFromOutside(IOperation instance, Microsoft.CodeAnalysis.Text.TextSpan span)
        {
            while (true)
            {
                switch (instance)
                {
                    case IInstanceReferenceOperation:
                        return true;
                    case IParameterReferenceOperation parameter:
                        return !parameter.Parameter.Locations.Any(l => span.Contains(l.SourceSpan));
                    case ILocalReferenceOperation local:
                        return !local.Local.Locations.Any(l => span.Contains(l.SourceSpan));
                    case IMemberReferenceOperation { Instance: { } inner }:
                        instance = inner;
                        continue;
                    case IMemberReferenceOperation:
                        return true; // a static member
                    default:
                        return false;
                }
            }
        }

        private static bool SameToken(IOperation used, IOperation? passed)
        {
            passed = Unwrap(passed);
            if (passed == null) return false;
            return (used, passed) switch
            {
                (IParameterReferenceOperation a, IParameterReferenceOperation b) => SymbolEqualityComparer.Default.Equals(a.Parameter, b.Parameter),
                (ILocalReferenceOperation a, ILocalReferenceOperation b) => SymbolEqualityComparer.Default.Equals(a.Local, b.Local),
                _ => used.Syntax.ToString() == passed.Syntax.ToString(),
            };
        }
    }
}
