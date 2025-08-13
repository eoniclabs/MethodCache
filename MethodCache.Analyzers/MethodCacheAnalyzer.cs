using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace MethodCache.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class MethodCacheAnalyzer : DiagnosticAnalyzer
    {
        public const string CacheDiagnosticId = "MC0001";
        public const string InvalidateDiagnosticId = "MC0002";

        private static readonly LocalizableString CacheTitle = "MethodCache Analyzer";
        private static readonly LocalizableString CacheMessageFormat = "Method '{0}' is marked with [Cache] but is not virtual, abstract, or an interface implementation. Caching may not work as expected.";
        private static readonly LocalizableString CacheDescription = "Methods marked with [Cache] should be virtual, abstract, or implement an interface method to allow for proper interception and caching.";

        private static readonly LocalizableString InvalidateTitle = "MethodCache Invalidate Analyzer";
        private static readonly LocalizableString InvalidateMessageFormat = "Method '{0}' is marked with [CacheInvalidate] but is not an async method. Invalidation might not be fully effective if the decorated method performs asynchronous operations.";
        private static readonly LocalizableString InvalidateDescription = "Methods marked with [CacheInvalidate] should ideally be asynchronous (return Task or ValueTask) to ensure invalidation occurs after the decorated method completes its operations.";

        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor CacheRule = new DiagnosticDescriptor(CacheDiagnosticId, CacheTitle, CacheMessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: CacheDescription);
        private static readonly DiagnosticDescriptor InvalidateRule = new DiagnosticDescriptor(InvalidateDiagnosticId, InvalidateTitle, InvalidateMessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: InvalidateDescription);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(CacheRule, InvalidateRule); } }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Method);
        }

        private static void AnalyzeSymbol(SymbolAnalysisContext context)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;

            // Analyze CacheAttribute usage
            var cacheAttribute = methodSymbol.GetAttributes().FirstOrDefault(ad => IsCacheAttribute(ad.AttributeClass));
            if (cacheAttribute != null)
            {
                if (methodSymbol.ContainingType.TypeKind == TypeKind.Class && !methodSymbol.IsVirtual && !methodSymbol.IsOverride && !methodSymbol.IsAbstract)
                {
                    var implementsInterfaceMethod = methodSymbol.ContainingType.AllInterfaces.Any(iface =>
                        iface.GetMembers().OfType<IMethodSymbol>().Any(ifaceMethod =>
                            SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType.FindImplementationForInterfaceMember(ifaceMethod), methodSymbol)
                        )
                    );

                    if (!implementsInterfaceMethod)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(CacheRule, methodSymbol.Locations[0], methodSymbol.Name));
                    }
                }
            }

            // Analyze CacheInvalidateAttribute usage
            var invalidateAttribute = methodSymbol.GetAttributes().FirstOrDefault(ad => IsCacheInvalidateAttribute(ad.AttributeClass));
            if (invalidateAttribute != null)
            {
                // Check if the method returns Task or ValueTask (i.e., is async)
                if (methodSymbol.ReturnType is INamedTypeSymbol returnTypeSymbol &&
                    (returnTypeSymbol.ToDisplayString() == "System.Threading.Tasks.Task" ||
                     returnTypeSymbol.ToDisplayString().StartsWith("System.Threading.Tasks.Task<") ||
                     returnTypeSymbol.ToDisplayString() == "System.Threading.Tasks.ValueTask" ||
                     returnTypeSymbol.ToDisplayString().StartsWith("System.Threading.Tasks.ValueTask<")))
                {
                    // This is an async method, which is generally fine for invalidation.
                }
                else
                {
                    // If it's not async, and it's not void, it might be problematic if invalidation is intended to be async.
                    // For now, we'll just warn if it's not async and not void.
                    if (methodSymbol.ReturnsVoid == false)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(InvalidateRule, methodSymbol.Locations[0], methodSymbol.Name));
                    }
                }
            }
        }

        private static bool IsCacheAttribute(INamedTypeSymbol? attributeClass)
        {
            return attributeClass?.Name == "CacheAttribute" &&
                   (attributeClass.ContainingNamespace?.ToDisplayString() == "MethodCache.Core" ||
                    attributeClass.ToDisplayString() == "MethodCache.Core.CacheAttribute");
        }

        private static bool IsCacheInvalidateAttribute(INamedTypeSymbol? attributeClass)
        {
            return attributeClass?.Name == "CacheInvalidateAttribute" &&
                   (attributeClass.ContainingNamespace?.ToDisplayString() == "MethodCache.Core" ||
                    attributeClass.ToDisplayString() == "MethodCache.Core.CacheInvalidateAttribute");
        }
    }
}
