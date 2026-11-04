using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0026: the implementation of a cached interface takes per-request state, such as the current user, in
    /// its constructor. A cached result is shared by every caller and may be loaded outside the request (a refresh or a
    /// fail-safe retry), so a result that depends on that state can reach another user.
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
            description: "The cache key is built from the method's arguments only. State a service reads from the current request, such as HttpContext, the signed-in user or a per-request database connection with row-level security, is not part of the key, and refresh-ahead and fail-safe loads run in a new scope without a request.",
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

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        }

        private static void AnalyzeType(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;

            var cached = type.AllInterfaces.FirstOrDefault(HasCachedMethod);
            if (cached is null) return;

            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor.IsImplicitlyDeclared || constructor.DeclaredAccessibility == Accessibility.Private) continue;
                foreach (var parameter in constructor.Parameters)
                {
                    if (!IsRequestState(parameter.Type)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(Rule, parameter.Locations.FirstOrDefault() ?? type.Locations[0],
                        type.Name, cached.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                        parameter.Name, parameter.Type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
                }
            }
        }

        private static bool HasCachedMethod(INamedTypeSymbol @interface)
            => @interface.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }));

        private static bool IsRequestState(ITypeSymbol type)
        {
            for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                if (RequestStateTypes.Contains(current.OriginalDefinition.ToDisplayString())) return true;
            }
            return type.AllInterfaces.Any(i => RequestStateTypes.Contains(i.OriginalDefinition.ToDisplayString()));
        }
    }
}
