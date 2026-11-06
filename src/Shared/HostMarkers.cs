using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MethodCache.Internal
{
    // Compiled into the analyzer and the generator: the generator marks a project's assembly when it has cached services
    // or calls AddMethodCache, and MC0030 in a program that references it reads the marks.
    internal static class HostMarkers
    {
        /// <summary><c>[assembly: AssemblyMetadata(CachedServices, "true")]</c>: the project has generated cached services.</summary>
        internal const string CachedServices = "MethodCache.CachedServices";

        /// <summary><c>[assembly: AssemblyMetadata(CallsAddMethodCache, "true")]</c>: the project's code calls AddMethodCache.</summary>
        internal const string CallsAddMethodCache = "MethodCache.CallsAddMethodCache";

        /// <summary>A call that may be <c>AddMethodCache</c>, told from its syntax alone.</summary>
        public static bool IsCandidate(SyntaxNode node)
            => node is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation && invocation.Expression switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == "AddMethodCache",
                Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax name => name.Identifier.ValueText == "AddMethodCache",
                _ => false,
            };

        /// <summary>MethodCache's <c>services.AddMethodCache(...)</c>, not another library's method of that name.</summary>
        public static bool IsAddMethodCache(ISymbol? symbol)
            => symbol is IMethodSymbol { Name: "AddMethodCache" } method
               && (method.ReducedFrom ?? method).ContainingType is
               {
                   Name: "MethodCacheServiceCollectionExtensions",
                   ContainingNamespace: { Name: "DependencyInjection", ContainingNamespace: { Name: "Extensions", ContainingNamespace: { Name: "Microsoft" } } },
               } type
               && type.ContainingAssembly?.Name == "MethodCache.Core";

        /// <summary>Whether a referenced assembly carries the mark.</summary>
        public static bool Has(IAssemblySymbol assembly, string key)
            => assembly.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: "AssemblyMetadataAttribute" }
                && attribute.ConstructorArguments.Length == 2
                && attribute.ConstructorArguments[0].Value as string == key);
    }
}
