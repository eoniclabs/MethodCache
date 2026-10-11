#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Utilities ========================
        private static class Utils
        {
            internal static readonly SymbolDisplayFormat FullyQualifiedFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType,
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included);

            internal static string GetFullyQualifiedName(ITypeSymbol type)
                => type.ToDisplayString(FullyQualifiedFormat);

            /// <summary>
            /// The namespace of an interface's decorator: the interface's own, or MethodCache.Generated for an
            /// interface in the global namespace, such as one declared next to top-level statements.
            /// </summary>
            internal static string GetDecoratorNamespace(INamedTypeSymbol interfaceSymbol)
            {
                var ns = interfaceSymbol.ContainingNamespace is { IsGlobalNamespace: false } containing
                    ? containing.ToDisplayString()
                    : null;

                // A decorator for another assembly's interface goes under MethodCache.Generated, so it can't clash
                // with a type that assembly declares next to the interface
                if (IsFromReferencedAssembly(interfaceSymbol))
                {
                    // Nested interfaces add their containing types, so Outer1.IClient and Outer2.IClient don't collide
                    var result = ns is null ? "MethodCache.Generated" : "MethodCache.Generated." + ns;
                    var containingTypes = new List<string>();
                    for (var type = interfaceSymbol.ContainingType; type != null; type = type.ContainingType)
                    {
                        containingTypes.Insert(0, type.Name);
                    }

                    return containingTypes.Count == 0 ? result : result + "." + string.Join(".", containingTypes);
                }

                return ns ?? "MethodCache.Generated";
            }

            /// <summary>An interface with static abstract members can't be a service type or get a decorator.</summary>
            internal static bool HasStaticAbstractMembers(INamedTypeSymbol interfaceSymbol)
                => new[] { interfaceSymbol }.Concat(interfaceSymbol.AllInterfaces).SelectMany(i => i.GetMembers()).Any(m => m.IsStatic && m.IsAbstract);

            internal static bool IsFromReferencedAssembly(INamedTypeSymbol interfaceSymbol)
                => interfaceSymbol.Locations.All(l => l.IsInMetadata);

            internal static string GetDecoratorFullName(INamedTypeSymbol interfaceSymbol)
                => $"global::{GetDecoratorNamespace(interfaceSymbol)}.{GetDecoratorClassName(interfaceSymbol)}";

            /// <summary>
            /// The decorator's class name. A nested interface adds its containing types (A.IService becomes
            /// A_IServiceDecorator), so A.IService and B.IService in one namespace get different decorators. Another
            /// assembly's interface already gets them in its namespace.
            /// </summary>
            internal static string GetDecoratorClassName(INamedTypeSymbol interfaceSymbol)
            {
                var name = interfaceSymbol.Name + "Decorator";
                if (IsFromReferencedAssembly(interfaceSymbol))
                {
                    return name;
                }

                for (var type = interfaceSymbol.ContainingType; type != null; type = type.ContainingType)
                {
                    name = type.Name + "_" + name;
                }

                return name;
            }

            /// <summary>
            /// A source file name unique per interface: namespace, containing types and generic arity included, so
            /// App.Users.IService, App.Orders.IService, IRepo and IRepo&lt;T&gt; never collide.
            /// </summary>
            internal static string GetDecoratorHintName(INamedTypeSymbol interfaceSymbol)
            {
                var arity = interfaceSymbol.Arity > 0 ? "`" + interfaceSymbol.Arity : string.Empty;
                return $"{GetDecoratorNamespace(interfaceSymbol)}.{GetDecoratorClassName(interfaceSymbol)}{arity}.g.cs";
            }

            /// <summary>A parameter or member name as C# source: keywords such as <c>class</c> become <c>@class</c>.</summary>
            internal static string Identifier(string name)
                => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
                    ? "@" + name
                    : name;

            /// <summary>A string as a C# literal, with quotes, backslashes and control characters escaped.</summary>
            internal static string Literal(string value)
                => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

            /// <summary>
            /// The method's policy id, matching MethodIds.For at runtime: the declaring interface's definition and the
            /// method name, plus the parameter types when the name is overloaded, such as <c>App.IUsers.Find(int)</c>.
            /// </summary>
            internal static string GetMethodId(IMethodSymbol method)
            {
                var definition = method.OriginalDefinition;
                return definition.ContainingType.ToDisplayString(TypeIdFormat) + "." + GetMethodName(definition);
            }

            /// <summary>The method's name in ids and keys: <c>Find</c>, or <c>Find(int)</c> when it is overloaded.</summary>
            internal static string GetMethodName(IMethodSymbol method)
            {
                var definition = method.OriginalDefinition;
                return IsOverloaded(definition)
                    ? definition.Name + "(" + string.Join(", ", definition.Parameters.Select(p => ParameterTypeName(p.Type))) + ")"
                    : definition.Name;
            }

            /// <summary>Whether the declaring interface has more than one method with this name.</summary>
            internal static bool IsOverloaded(IMethodSymbol method)
                => method.OriginalDefinition.ContainingType.GetMembers(method.Name)
                    .OfType<IMethodSymbol>()
                    .Count(m => m.MethodKind == MethodKind.Ordinary) > 1;

            private static readonly SymbolDisplayFormat TypeIdFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

            private static readonly SymbolDisplayFormat ParameterTypeIdFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

            /// <summary>A parameter type as MethodIds writes it: C# keywords, qualified names, T?, T[].</summary>
            private static string ParameterTypeName(ITypeSymbol type)
            {
                switch (type)
                {
                    case IArrayTypeSymbol array:
                        return ParameterTypeName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
                    case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable:
                        return ParameterTypeName(nullable.TypeArguments[0]) + "?";
                }

                var keyword = type.SpecialType switch
                {
                    SpecialType.System_Boolean => "bool",
                    SpecialType.System_Byte => "byte",
                    SpecialType.System_SByte => "sbyte",
                    SpecialType.System_Int16 => "short",
                    SpecialType.System_UInt16 => "ushort",
                    SpecialType.System_Int32 => "int",
                    SpecialType.System_UInt32 => "uint",
                    SpecialType.System_Int64 => "long",
                    SpecialType.System_UInt64 => "ulong",
                    SpecialType.System_Single => "float",
                    SpecialType.System_Double => "double",
                    SpecialType.System_Decimal => "decimal",
                    SpecialType.System_Char => "char",
                    SpecialType.System_String => "string",
                    SpecialType.System_Object => "object",
                    _ => null
                };
                if (keyword != null)
                {
                    return keyword;
                }

                return type.ToDisplayString(ParameterTypeIdFormat);
            }

            internal static bool IsCancellationToken(ITypeSymbol type)
                => type.Name == nameof(CancellationToken) &&
                   type.ContainingNamespace?.ToDisplayString() == "System.Threading";

            internal static bool IsTask(ITypeSymbol type, out ITypeSymbol? innerType)
            {
                if (type is INamedTypeSymbol namedType)
                {
                    if (namedType.IsGenericType &&
                        namedType.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>")
                    {
                        innerType = namedType.TypeArguments[0];
                        return true;
                    }

                    if (namedType.ToDisplayString() == "System.Threading.Tasks.Task")
                    {
                        innerType = null; // Non-generic Task is conceptually void
                        return true;
                    }
                }

                innerType = null;
                return false;
            }

            internal static bool IsValueTask(ITypeSymbol type, out ITypeSymbol? innerType)
            {
                if (type is INamedTypeSymbol namedType)
                {
                    if (namedType.IsGenericType &&
                        namedType.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.ValueTask<TResult>")
                    {
                        innerType = namedType.TypeArguments[0];
                        return true;
                    }

                    if (namedType.ToDisplayString() == "System.Threading.Tasks.ValueTask")
                    {
                        innerType = null; // Non-generic ValueTask is conceptually void
                        return true;
                    }
                }

                innerType = null;
                return false;
            }

            // Fully qualified so generated code never depends on the user's usings, and with nullable annotations
            // so the decorator's signatures match the interface it implements exactly (no CS8613/CS8614 warnings)
            private static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat.FullyQualifiedFormat
                .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

            internal static string GetReturnTypeForSignature(ITypeSymbol type) => type.ToDisplayString(SignatureFormat);
        }
    }
}

