using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MethodCache attributes that have no effect or invalid values while the code is being written.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MethodCacheAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// The id of the diagnostic for [Cache] or [CacheInvalidate] on a class member (MC0001).
        /// </summary>
        public const string AttributeOnClassDiagnosticId = "MC0001";

        /// <summary>
        /// The id of the diagnostic for a time span that is not a positive TimeSpan (MC0004).
        /// </summary>
        public const string InvalidTimeSpanDiagnosticId = "MC0004";

        private const string Category = "Usage";
        private const string AttributeNamespace = "MethodCache";

        // Each id has its own heading in the diagnostics guide, so IDEs and build output link straight to the fix
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor AttributeOnClassRule = new DiagnosticDescriptor(
            AttributeOnClassDiagnosticId,
            "MethodCache attribute on a class member has no effect",
            "[{0}] on '{1}' has no effect: MethodCache caches interface methods. Put [{0}] on the interface method instead.",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The source generator decorates interfaces, so [Cache] and [CacheInvalidate] are read from interface methods only. On a class, struct or record member they are ignored and the method is not cached.",
            helpLinkUri: HelpLink(AttributeOnClassDiagnosticId));

        internal static readonly DiagnosticDescriptor InvalidTimeSpanRule = new DiagnosticDescriptor(
            InvalidTimeSpanDiagnosticId,
            "Invalid time span",
            "{0} = \"{1}\" is not a positive time span; use a value such as \"00:05:00\"",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Duration, SlidingExpiration, RefreshAhead, FailSafe and FailSafeTimeout on [Cache] and [assembly: CacheInterface] must be positive TimeSpan strings, for example \"00:05:00\" or \"1.00:00:00\". FailSafe also accepts \"off\".",
            helpLinkUri: HelpLink(InvalidTimeSpanDiagnosticId));

        private static readonly string[] TimeSpanProperties = { "Duration", "SlidingExpiration", "RefreshAhead", "FailSafe", "FailSafeTimeout" };

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(AttributeOnClassRule, InvalidTimeSpanRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
            context.RegisterSyntaxNodeAction(AnalyzeAssemblyAttributes, SyntaxKind.AttributeList);
        }

        private static void AnalyzeMethod(SymbolAnalysisContext context)
        {
            var method = (IMethodSymbol)context.Symbol;
            var onInterface = method.ContainingType?.TypeKind == TypeKind.Interface;

            foreach (var attribute in method.GetAttributes())
            {
                var isCache = IsMethodCacheAttribute(attribute.AttributeClass, "CacheAttribute");
                if (!isCache && !IsMethodCacheAttribute(attribute.AttributeClass, "CacheInvalidateAttribute"))
                {
                    continue;
                }

                if (!onInterface)
                {
                    var name = isCache ? "Cache" : "CacheInvalidate";
                    Report(context, AttributeLocation(attribute, context.CancellationToken) ?? method.Locations[0], AttributeOnClassRule, name, method.Name);
                }

                if (isCache)
                {
                    foreach (var (property, text) in InvalidTimeSpans(attribute))
                    {
                        Report(context, AttributeLocation(attribute, context.CancellationToken) ?? method.Locations[0], InvalidTimeSpanRule, property, text);
                    }
                }
            }
        }

        private static void AnalyzeAssemblyAttributes(SyntaxNodeAnalysisContext context)
        {
            // [assembly: CacheInterface] carries the same time spans as [Cache], on the assembly instead of a method.
            // Read per attribute list, so the error shows while typing and not only in a full build.
            var list = (AttributeListSyntax)context.Node;
            if (list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) != true)
            {
                return;
            }

            foreach (var attribute in context.Compilation.Assembly.GetAttributes())
            {
                if (!IsMethodCacheAttribute(attribute.AttributeClass, "CacheInterfaceAttribute") ||
                    attribute.ApplicationSyntaxReference is not { } reference ||
                    reference.SyntaxTree != list.SyntaxTree ||
                    !list.Span.Contains(reference.Span))
                {
                    continue;
                }

                var location = reference.GetSyntax(context.CancellationToken).GetLocation();
                foreach (var (property, text) in InvalidTimeSpans(attribute))
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidTimeSpanRule, location, property, text));
                }
            }
        }

        private static IEnumerable<(string Property, string Text)> InvalidTimeSpans(AttributeData attribute)
        {
            foreach (var argument in attribute.NamedArguments)
            {
                if (Array.IndexOf(TimeSpanProperties, argument.Key) >= 0 &&
                    argument.Value.Value is string text &&
                    !(argument.Key == "FailSafe" && string.Equals(text, "off", StringComparison.OrdinalIgnoreCase)) &&
                    (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) || value <= TimeSpan.Zero))
                {
                    yield return (argument.Key, text);
                }
            }
        }

        private static bool IsMethodCacheAttribute(INamedTypeSymbol? attributeClass, string name)
            => attributeClass?.Name == name && attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace;

        private static Location? AttributeLocation(AttributeData attribute, System.Threading.CancellationToken cancellationToken)
            => attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation();

        private static void Report(SymbolAnalysisContext context, Location location, DiagnosticDescriptor descriptor, params object[] messageArgs)
            => context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
    }
}
