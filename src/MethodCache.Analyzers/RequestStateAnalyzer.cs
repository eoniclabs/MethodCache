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
            description: "The cache key is built from the method's arguments only. State a service reads from the current request, such as HttpContext, the signed-in user or a per-request database connection with row-level security, is not part of the key, and refresh-ahead and fail-safe loads run in a new scope without a request. A class the implementation takes, or creates through a factory such as IDbContextFactory<T>, is checked one level down: a DbContext whose constructor takes IHttpContextAccessor is reported too.",
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
                    var taken = Display(parameter.Type);
                    if (!IsRequestState(parameter.Type))
                    {
                        if (RequestStateDependency(parameter.Type) is not var (dependency, state)) continue;
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

        /// <summary>
        /// A class the parameter's type is, or names as a type argument (the context of <c>IDbContextFactory&lt;T&gt;</c>),
        /// whose constructor takes per-request state. One level down only: past that the analyzer can't tell what runs.
        /// </summary>
        private static (ITypeSymbol Dependency, ITypeSymbol State)? RequestStateDependency(ITypeSymbol type)
        {
            var candidates = new List<ITypeSymbol> { type };
            if (type is INamedTypeSymbol { IsGenericType: true } generic) candidates.AddRange(generic.TypeArguments);
            foreach (var candidate in candidates)
            {
                if (candidate is not INamedTypeSymbol { TypeKind: TypeKind.Class } dependency || IsRequestState(dependency)) continue;
                foreach (var constructor in dependency.InstanceConstructors)
                {
                    if (constructor.IsImplicitlyDeclared || constructor.DeclaredAccessibility == Accessibility.Private) continue;
                    var state = constructor.Parameters.FirstOrDefault(p => IsRequestState(p.Type));
                    if (state != null) return (dependency, state.Type);
                }
            }
            return null;
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
