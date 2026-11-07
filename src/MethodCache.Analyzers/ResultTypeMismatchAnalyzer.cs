using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0033: <c>CacheWhen&lt;T&gt;</c>, <c>DurationFor&lt;T&gt;</c>, <c>SlidingFor&lt;T&gt;</c> or <c>TagsFor&lt;T&gt;</c> on the options of a call whose result can
    /// never be a <c>T</c>, such as <c>CacheWhen&lt;string&gt;</c> for a method that returns <c>int</c>. MethodCache
    /// rejects it when the call runs or the host starts; this finds it while building.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ResultTypeMismatchAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0033",
            "CacheWhen<T>, DurationFor<T>, SlidingFor<T> or TagsFor<T> is for another result type",
            "{0}<{1}> can't check the {2} result of this call, so the call fails when it runs. Use {0}<{2}>.",
            "Usage",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "CacheWhen<T>, DurationFor<T>, SlidingFor<T> and TagsFor<T> on CacheEntryOptions.Builder get each result as a T. Set inside Method(...).Configure(...) or passed to GetOrCreateAsync, GetOrCreateManyAsync or SetAsync, a T that the call's result can never be fails at startup or on the first call. Reported when neither type converts to the other.",
            helpLinkUri: HelpLink("MC0033"));

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName("MethodCache.CacheEntryOptions+Builder") is not { } builder) return;
                start.RegisterOperationAction(operation => Analyze(operation, builder), OperationKind.Invocation);
            });
        }

        private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol builder)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var method = invocation.TargetMethod;
            if (method.Name is not ("CacheWhen" or "DurationFor" or "SlidingFor" or "TagsFor") || method.TypeArguments.Length != 1
                || !SymbolEqualityComparer.Default.Equals(method.ContainingType, builder)) return;

            // The options lambda the builder is the parameter of, as in o => o.WithDuration(...).CacheWhen<T>(...)
            var instance = invocation.Instance;
            while (instance is IInvocationOperation { Instance: { } inner } chained && SymbolEqualityComparer.Default.Equals(chained.Type, builder)) instance = inner;
            if (instance is not IParameterReferenceOperation { Parameter.ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } lambda }) return;
            var function = invocation.Parent;
            while (function != null && !(function is IAnonymousFunctionOperation anonymous && SymbolEqualityComparer.Default.Equals(anonymous.Symbol, lambda))) function = function.Parent;
            if (function?.Parent is not IDelegateCreationOperation { Parent: IArgumentOperation { Parent: IInvocationOperation call } }) return;

            if (ResultType(call) is not { } result) return;
            var expected = method.TypeArguments[0];
            if (!IsKnown(expected) || !IsKnown(result) || Fits(context.Compilation, result, expected) || Fits(context.Compilation, expected, result)) return;

            var location = invocation.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax
            {
                Expression: Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax access,
            } ? access.Name.GetLocation() : invocation.Syntax.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, method.Name,
                expected.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), result.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        /// <summary>
        /// The result type the options apply to: <c>TResult</c> of <c>Method(...).Configure(...)</c>, or <c>T</c> of
        /// <c>GetOrCreateAsync&lt;T&gt;</c>, <c>GetOrCreateManyAsync</c> and <c>SetAsync&lt;T&gt;</c>.
        /// </summary>
        private static ITypeSymbol? ResultType(IInvocationOperation call)
        {
            var target = call.TargetMethod;
            var type = target.ContainingType;
            if (type is not { ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }) return null;
            return target.Name switch
            {
                "Configure" when type is { Name: "IFluentMethodConfiguration", TypeArguments.Length: 1 } => type.TypeArguments[0],
                "GetOrCreateAsync" or "GetOrCreateManyAsync" or "SetAsync" when type.Name == "CacheManagerExtensions" && target.TypeArguments.Length > 0 => target.TypeArguments.Last(),
                _ => null,
            };
        }

        // A type parameter or a type that doesn't compile can be anything at run time
        private static bool IsKnown(ITypeSymbol type) => type.TypeKind is not (TypeKind.TypeParameter or TypeKind.Error);

        /// <summary>
        /// Whether a <paramref name="from"/> value can be seen as a <paramref name="to"/>, as the check at run time takes it:
        /// the same type, a base type or interface, boxing, or the nullable form of the same value type. A numeric
        /// conversion such as <c>int</c> to <c>long</c> doesn't count, as the boxed value isn't a <c>long</c>.
        /// </summary>
        private static bool Fits(Compilation compilation, ITypeSymbol from, ITypeSymbol to)
        {
            var conversion = compilation.ClassifyConversion(from, to);
            if (conversion.IsIdentity || (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing))) return true;
            return conversion.IsNullable && conversion.IsImplicit && to is INamedTypeSymbol { TypeArguments.Length: 1 } wrapper
                && SymbolEqualityComparer.Default.Equals(wrapper.TypeArguments[0], from);
        }
    }
}
