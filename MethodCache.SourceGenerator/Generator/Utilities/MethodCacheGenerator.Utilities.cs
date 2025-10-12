#nullable enable

using System.Threading;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator.Generator.Utilities
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
        
            internal static readonly SymbolDisplayFormat MethodIdFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);
        
            internal static string GetFullyQualifiedName(ITypeSymbol type)
                => type.ToDisplayString(FullyQualifiedFormat);
        
            internal static string GetMethodId(IMethodSymbol method)
                => method.ToDisplayString(MethodIdFormat);
        
            internal static bool IsCancellationToken(ITypeSymbol type)
                => type.Name == nameof(CancellationToken) &&
                   type.ContainingNamespace?.ToDisplayString() == "System.Threading";
        
            internal static bool IsTask(ITypeSymbol type, out string? typeArg)
            {
                typeArg = null;
                if (type is INamedTypeSymbol nt && nt.Name == "Task" &&
                    nt.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
                {
                    if (nt.IsGenericType && nt.TypeArguments.Length == 1)
                    {
                        typeArg = Utils.GetReturnTypeForSignature(nt.TypeArguments[0]);
                        return true;
                    }
                }
                return false;
            }
        
            internal static bool IsValueTask(ITypeSymbol type, out string? typeArg)
            {
                typeArg = null;
                if (type is INamedTypeSymbol nt && nt.Name == "ValueTask" &&
                    nt.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
                {
                    if (nt.IsGenericType && nt.TypeArguments.Length == 1)
                    {
                        typeArg = Utils.GetReturnTypeForSignature(nt.TypeArguments[0]);
                        return true;
                    }
                }
                return false;
            }
        
            internal static string GetReturnTypeForSignature(ITypeSymbol type)
            {
                // Handle nullable value types first (e.g., int?, bool?)
                if (type is INamedTypeSymbol namedType && namedType.IsGenericType && 
                    namedType.OriginalDefinition?.SpecialType == SpecialType.System_Nullable_T)
                {
                    var underlyingType = namedType.TypeArguments[0];
                    var underlyingTypeName = GetReturnTypeForSignature(underlyingType);
                    return $"{underlyingTypeName}?";
                }
        
                // Use C# keywords for common types instead of fully qualified names
                if (type.SpecialType == SpecialType.System_Void) return "void";
                if (type.SpecialType == SpecialType.System_String) return "string";
                if (type.SpecialType == SpecialType.System_Int32) return "int";
                if (type.SpecialType == SpecialType.System_Boolean) return "bool";
                if (type.SpecialType == SpecialType.System_Double) return "double";
                if (type.SpecialType == SpecialType.System_Single) return "float";
                if (type.SpecialType == SpecialType.System_Int64) return "long";
                if (type.SpecialType == SpecialType.System_Decimal) return "decimal";
                if (type.SpecialType == SpecialType.System_Byte) return "byte";
                if (type.SpecialType == SpecialType.System_SByte) return "sbyte";
                if (type.SpecialType == SpecialType.System_Int16) return "short";
                if (type.SpecialType == SpecialType.System_UInt16) return "ushort";
                if (type.SpecialType == SpecialType.System_UInt32) return "uint";
                if (type.SpecialType == SpecialType.System_UInt64) return "ulong";
                if (type.SpecialType == SpecialType.System_Char) return "char";
                if (type.SpecialType == SpecialType.System_Object) return "object";
                
                // Handle generic types
                if (type is INamedTypeSymbol genericType)
                {
                    // Handle Task types
                    if (genericType.Name == "Task" && genericType.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
                    {
                        if (genericType.TypeArguments.Length == 1)
                        {
                            var innerType = GetReturnTypeForSignature(genericType.TypeArguments[0]);
                            return $"System.Threading.Tasks.Task<{innerType}>";
                        }
                        return "System.Threading.Tasks.Task";
                    }
                    
                    // Handle ValueTask types
                    if (genericType.Name == "ValueTask" && genericType.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
                    {
                        if (genericType.TypeArguments.Length == 1)
                        {
                            var innerType = GetReturnTypeForSignature(genericType.TypeArguments[0]);
                            return $"System.Threading.Tasks.ValueTask<{innerType}>";
                        }
                        return "System.Threading.Tasks.ValueTask";
                    }
                    
                    // Handle common generic collection types
                    var ns = genericType.ContainingNamespace?.ToDisplayString();
                    if (ns == "System.Collections.Generic")
                    {
                        if (genericType.Name == "List" && genericType.TypeArguments.Length == 1)
                        {
                            var innerType = GetReturnTypeForSignature(genericType.TypeArguments[0]);
                            return $"List<{innerType}>";
                        }
                        if (genericType.Name == "Dictionary" && genericType.TypeArguments.Length == 2)
                        {
                            var keyType = GetReturnTypeForSignature(genericType.TypeArguments[0]);
                            var valueType = GetReturnTypeForSignature(genericType.TypeArguments[1]);
                            return $"Dictionary<{keyType}, {valueType}>";
                        }
                        if (genericType.Name == "IEnumerable" && genericType.TypeArguments.Length == 1)
                        {
                            var innerType = GetReturnTypeForSignature(genericType.TypeArguments[0]);
                            return $"IEnumerable<{innerType}>";
                        }
                        if (genericType.Name == "IList" && genericType.TypeArguments.Length == 1)
                        {
                            var innerType = GetReturnTypeForSignature(genericType.TypeArguments[0]);
                            return $"IList<{innerType}>";
                        }
                        if (genericType.Name == "ICollection" && genericType.TypeArguments.Length == 1)
                        {
                            var innerType = GetReturnTypeForSignature(genericType.TypeArguments[0]);
                            return $"ICollection<{innerType}>";
                        }
                    }
                }
                
                // For complex types, use simple qualified names
                return type.ToDisplayString(new SymbolDisplayFormat(
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters));
            }
        }
    }
}

