using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        public const string ConfigurationOverrideDiagnosticId = "MC0003";

        private static readonly LocalizableString CacheTitle = "MethodCache Analyzer";
        private static readonly LocalizableString CacheMessageFormat = "Method '{0}' is marked with [Cache] but is not virtual, abstract, or an interface implementation. Caching may not work as expected.";
        private static readonly LocalizableString CacheDescription = "Methods marked with [Cache] should be virtual, abstract, or implement an interface method to allow for proper interception and caching.";

        private static readonly LocalizableString InvalidateTitle = "MethodCache Invalidate Analyzer";
        private static readonly LocalizableString InvalidateMessageFormat = "Method '{0}' is marked with [CacheInvalidate] but is not an async method. Invalidation might not be fully effective if the decorated method performs asynchronous operations.";
        private static readonly LocalizableString InvalidateDescription = "Methods marked with [CacheInvalidate] should ideally be asynchronous (return Task or ValueTask) to ensure invalidation occurs after the decorated method completes its operations.";

        private static readonly LocalizableString ConfigurationOverrideTitle = "Cache Attribute Override Warning";
        private static readonly LocalizableString ConfigurationOverrideMessageFormat = "Method '{0}' has a [Cache] attribute, but programmatic configuration can override these settings. Consider using configuration-only approach for consistency.";
        private static readonly LocalizableString ConfigurationOverrideDescription = "When using AddMethodCache with a configuration delegate, programmatic settings will override attribute-based cache settings. This may cause confusion when the attribute specifies different values than what is actually used at runtime.";

        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor CacheRule = new DiagnosticDescriptor(CacheDiagnosticId, CacheTitle, CacheMessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: CacheDescription);
        private static readonly DiagnosticDescriptor InvalidateRule = new DiagnosticDescriptor(InvalidateDiagnosticId, InvalidateTitle, InvalidateMessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: InvalidateDescription);
        internal static readonly DiagnosticDescriptor ConfigurationOverrideRule = new DiagnosticDescriptor(ConfigurationOverrideDiagnosticId, ConfigurationOverrideTitle, ConfigurationOverrideMessageFormat, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description: ConfigurationOverrideDescription);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(CacheRule, InvalidateRule, ConfigurationOverrideRule); } }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Method);
            context.RegisterCompilationStartAction(OnCompilationStarted);
        }
        
        private static void OnCompilationStarted(CompilationStartAnalysisContext context)
        {
            var configurationOverrideTracker = new ConfigurationOverrideTracker();
            
            context.RegisterSyntaxNodeAction(configurationOverrideTracker.AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSymbolAction(configurationOverrideTracker.AnalyzeCacheMethod, SymbolKind.Method);
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
    
    internal class ConfigurationOverrideTracker
    {
        private bool _hasConfigurationOverride = false;

        public void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            
            // Check if this is an AddMethodCache call with a configuration delegate
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.ValueText == "AddMethodCache" &&
                invocation.ArgumentList.Arguments.Count > 0)
            {
                // Check if first argument is a lambda or delegate (configuration parameter)
                var firstArg = invocation.ArgumentList.Arguments[0];
                if (firstArg.Expression is LambdaExpressionSyntax ||
                    firstArg.Expression is IdentifierNameSyntax ||
                    firstArg.Expression is AnonymousFunctionExpressionSyntax)
                {
                    _hasConfigurationOverride = true;
                }
            }
        }

        public void AnalyzeCacheMethod(SymbolAnalysisContext context)
        {
            if (!_hasConfigurationOverride) return;
            
            var methodSymbol = (IMethodSymbol)context.Symbol;
            var cacheAttribute = methodSymbol.GetAttributes().FirstOrDefault(ad => IsCacheAttribute(ad.AttributeClass));
            
            if (cacheAttribute != null)
            {
                // Only warn if the method is part of an interface (likely to be auto-registered)
                if (methodSymbol.ContainingType.TypeKind == TypeKind.Interface)
                {
                    var diagnostic = Diagnostic.Create(
                        MethodCacheAnalyzer.ConfigurationOverrideRule,
                        cacheAttribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? methodSymbol.Locations[0],
                        methodSymbol.Name);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
        
        private static bool IsCacheAttribute(INamedTypeSymbol? attributeClass)
        {
            return attributeClass?.Name == "CacheAttribute" &&
                   (attributeClass.ContainingNamespace?.ToDisplayString() == "MethodCache.Core" ||
                    attributeClass.ToDisplayString() == "MethodCache.Core.CacheAttribute");
        }
    }
}
