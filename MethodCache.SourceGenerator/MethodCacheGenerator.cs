#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.SourceGenerator
{
    /// <summary>
    /// Source generator for MethodCache that creates decorator classes for interfaces with [Cache] and [CacheInvalidate] attributes.
    /// 
    /// Features:
    /// - Generates async/sync caching decorators for interface methods
    /// - Supports cache invalidation by tags with dynamic parameter substitution
    /// - Handles generic methods and complex type constraints
    /// - Generates dependency injection extensions
    /// - Creates centralized method registry for configuration
    /// - Proper async/await patterns for invalidation methods
    /// - Enhanced nullable type handling (int?, bool?, etc.)
    /// 
    /// Dynamic Tag Substitution:
    /// Use {parameterName} in invalidation tags to dynamically substitute method parameter values:
    /// [CacheInvalidate(Tags = new[] { "user:{userId}", "tenant:{tenantId}" })]
    /// This will generate: string.Format("user:{0}", userId?.ToString() ?? "null")
    /// 
    /// Async Patterns:
    /// - Async invalidation methods properly await the decorated method before invalidating
    /// - Cache invalidation only occurs after successful method execution
    /// - Proper ConfigureAwait(false) usage throughout generated code
    /// 
    /// Sync-over-Async Warnings:
    /// - Synchronous cached methods receive warnings about potential deadlocks
    /// - Generated code includes warnings about SynchronizationContext risks
    /// - Consider using async methods to avoid deadlock potential
    /// 
    /// Limitations:
    /// - Only works on interface methods (not class methods)
    /// - Does not support ref/out/in parameters  
    /// - Does not support void return types for caching
    /// - Does not support non-generic Task/ValueTask returns
    /// - Sync-over-async pattern may cause deadlocks in certain contexts
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class MethodCacheGenerator : IIncrementalGenerator
    {
        // ======================== Compiled Regex Patterns ========================
        private static readonly Regex DynamicTagParameterRegex = new(@"\{(\w+)\}", RegexOptions.Compiled);
        
        // ======================== Diagnostics ========================
        private static class Diagnostics
        {
            internal static readonly DiagnosticDescriptor UnsupportedVoidCache = new(
                id: "MCG001",
                title: "Caching not supported on void returns",
                messageFormat: "[Cache] attribute on method '{0}' with void return type is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedTaskCache = new(
                id: "MCG002",
                title: "Caching not supported on non-generic Task/ValueTask",
                messageFormat: "[Cache] attribute on method '{0}' with non-generic Task/ValueTask return type is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedRefParams = new(
                id: "MCG003",
                title: "Caching not supported with ref/out/in parameters",
                messageFormat: "[Cache] attribute on method '{0}' with ref/out/in parameters is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor AttributeConflict = new(
                id: "MCG004",
                title: "Conflicting cache attributes",
                messageFormat: "Method '{0}' cannot have both [Cache] and [CacheInvalidate] attributes",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor NoInvalidateTags = new(
                id: "MCG005",
                title: "CacheInvalidate has no tags",
                messageFormat: "[CacheInvalidate] on method '{0}' has no tags specified; invalidation will be a no-op",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedPointerType = new(
                id: "MCG006",
                title: "Caching not supported with pointer types",
                messageFormat: "[Cache] attribute on method '{0}' with pointer type parameters is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor UnsupportedRefLikeType = new(
                id: "MCG007",
                title: "Caching not supported with ref struct types",
                messageFormat: "[Cache] attribute on method '{0}' with ref struct parameters is not supported",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor SyncOverAsyncWarning = new(
                id: "MCG008",
                title: "Synchronous method caching may cause deadlocks",
                messageFormat: "Method '{0}' is synchronous but uses async caching infrastructure. This may cause deadlocks in environments with SynchronizationContext (ASP.NET Framework, WPF, WinForms). Consider making the method async or use with caution.",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true);

            internal static readonly DiagnosticDescriptor DynamicTagParameterNotFound = new(
                id: "MCG009",
                title: "Dynamic tag references unknown parameter",
                messageFormat: "[CacheInvalidate] tag '{0}' references parameter '{1}' which does not exist on method '{2}'",
                category: "MethodCacheGenerator",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);
        }

        // ======================== Core Generator ========================
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Look for methods with Cache attribute and get their containing interfaces
            var interfaceProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.Core.CacheAttribute",
                    predicate: static (node, _) => node is MethodDeclarationSyntax method && 
                                                   method.Parent is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => GetInterfaceInfoFromMethod(ctx, ct))
                .Where(static info => info != null)
                .Collect();

            // Also check for CacheInvalidate attributes on methods
            var invalidateProvider = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    fullyQualifiedMetadataName: "MethodCache.Core.CacheInvalidateAttribute",
                    predicate: static (node, _) => node is MethodDeclarationSyntax method && 
                                                   method.Parent is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => GetInterfaceInfoFromMethod(ctx, ct))
                .Where(static info => info != null)
                .Collect();

            // Combine both providers
            var combinedProvider = interfaceProvider.Combine(invalidateProvider)
                .Select(static (pair, _) =>
                {
                    var combined = pair.Left.Concat(pair.Right)
                        .Where(i => i != null)
                        .GroupBy(i => i!.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                        .Select(g => g.First())
                        .ToImmutableArray();
                    return combined;
                });

            context.RegisterSourceOutput(combinedProvider, Execute);
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<InterfaceInfo?> interfaces)
        {
            if (interfaces.IsDefaultOrEmpty) return;

            var validInterfaces = interfaces
                .Where(i => i != null)
                .Cast<InterfaceInfo>()
                .ToList();

            if (validInterfaces.Count == 0) return;

            // Report diagnostics
            foreach (var info in validInterfaces)
            {
                foreach (var diagnostic in info.Diagnostics)
                {
                    context.ReportDiagnostic(diagnostic);
                }
            }

            // Generate sources
            foreach (var info in validInterfaces)
            {
                var decoratorCode = DecoratorEmitter.Emit(info);
                context.AddSource($"{info.Symbol.Name}Decorator.g.cs", decoratorCode);
            }

            // Generate shared components
            if (validInterfaces.Any(i => i.CachedMethods.Any()))
            {
                var registryCode = RegistryEmitter.Emit(validInterfaces);
                context.AddSource("CacheMethodRegistry.g.cs", registryCode);
            }

            var extensionsCode = DIExtensionsEmitter.Emit(validInterfaces);
            context.AddSource("MethodCacheServiceCollectionExtensions.g.cs", extensionsCode);
        }

        // ======================== Discovery & Analysis ========================
        private static InterfaceInfo? GetInterfaceInfoFromMethod(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            // The target symbol is the method, we need to get the containing interface
            if (ctx.TargetSymbol is not IMethodSymbol methodSymbol)
                return null;

            var interfaceSymbol = methodSymbol.ContainingType;
            if (interfaceSymbol?.TypeKind != TypeKind.Interface)
                return null;

            return GetInterfaceInfo(interfaceSymbol);
        }

        private static InterfaceInfo? GetInterfaceInfo(INamedTypeSymbol interfaceSymbol)
        {

            var allMethods = GetAllInterfaceMethods(interfaceSymbol).ToList();
            if (allMethods.Count == 0)
                return null;

            var cachedMethods = new List<MethodModel>();
            var invalidateMethods = new List<MethodModel>();
            var diagnostics = new List<Diagnostic>();

            foreach (var method in allMethods)
            {
                if (method.MethodKind != MethodKind.Ordinary) continue;

                var methodToCheck = method.OriginalDefinition ?? method;
                var attributes = methodToCheck.GetAttributes();

                var cacheAttr = attributes.FirstOrDefault(a => IsCacheAttribute(a.AttributeClass));
                var invalidateAttr = attributes.FirstOrDefault(a => IsCacheInvalidateAttribute(a.AttributeClass));

                if (cacheAttr == null && invalidateAttr == null) continue;

                // Check for conflicts
                if (cacheAttr != null && invalidateAttr != null)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.AttributeConflict,
                        method.Locations.FirstOrDefault(),
                        method.ToDisplayString()));
                    continue;
                }

                if (cacheAttr != null)
                {
                    var validation = ValidateCacheMethod(method);
                    if (validation.diagnostic != null)
                    {
                        diagnostics.Add(validation.diagnostic);
                        if (!validation.canProceed) continue;
                    }
                    
                    // Check for sync-over-async warning
                    if (!Utils.IsTask(method.ReturnType, out _) && !Utils.IsValueTask(method.ReturnType, out _))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.SyncOverAsyncWarning,
                            method.Locations.FirstOrDefault(),
                            method.ToDisplayString()));
                    }
                    
                    cachedMethods.Add(new MethodModel(method, cacheAttr, null));
                }
                else if (invalidateAttr != null)
                {
                    var tags = ExtractTags(invalidateAttr);
                    if (!tags.Any())
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.NoInvalidateTags,
                            method.Locations.FirstOrDefault(),
                            method.ToDisplayString()));
                    }
                    else
                    {
                        // Validate dynamic tags
                        var dynamicTagDiagnostics = ValidateDynamicTags(tags, method);
                        diagnostics.AddRange(dynamicTagDiagnostics);
                    }
                    invalidateMethods.Add(new MethodModel(method, null, invalidateAttr));
                }
            }

            if (!cachedMethods.Any() && !invalidateMethods.Any() && !diagnostics.Any())
                return null;

            return new InterfaceInfo(
                interfaceSymbol,
                cachedMethods.ToImmutableArray(),
                invalidateMethods.ToImmutableArray(),
                diagnostics.ToImmutableArray());
        }

        private static (Diagnostic? diagnostic, bool canProceed) ValidateCacheMethod(IMethodSymbol method)
        {
            var location = method.Locations.FirstOrDefault();
            var methodName = method.ToDisplayString();

            // Check for void return
            if (method.ReturnsVoid)
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedVoidCache, location, methodName), false);
            }

            // Check for non-generic Task/ValueTask
            if (method.ReturnType is INamedTypeSymbol namedType)
            {
                var ns = namedType.ContainingNamespace?.ToDisplayString();
                if (ns == "System.Threading.Tasks" && !namedType.IsGenericType)
                {
                    if (namedType.Name == "Task" || namedType.Name == "ValueTask")
                    {
                        return (Diagnostic.Create(Diagnostics.UnsupportedTaskCache, location, methodName), false);
                    }
                }
            }

            // Check for ref/out/in parameters
            if (method.Parameters.Any(p => p.RefKind != RefKind.None))
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedRefParams, location, methodName), false);
            }

            // Check for pointer types
            if (method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Pointer))
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedPointerType, location, methodName), false);
            }

            // Check for ref struct types
            if (method.Parameters.Any(p => IsRefLikeType(p.Type)))
            {
                return (Diagnostic.Create(Diagnostics.UnsupportedRefLikeType, location, methodName), false);
            }

            return (null, true);
        }

        private static bool IsRefLikeType(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol namedType)
            {
                // Check for ref struct (Span<T>, ReadOnlySpan<T>, etc.)
                return namedType.IsRefLikeType;
            }
            return false;
        }

        private static IEnumerable<IMethodSymbol> GetAllInterfaceMethods(INamedTypeSymbol interfaceSymbol)
        {
            return interfaceSymbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Concat(interfaceSymbol.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>()))
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<IMethodSymbol>();
        }

        private static bool IsCacheAttribute(INamedTypeSymbol? attr)
            => attr?.Name == "CacheAttribute" &&
               attr.ContainingNamespace?.ToDisplayString() == "MethodCache.Core";

        private static bool IsCacheInvalidateAttribute(INamedTypeSymbol? attr)
            => attr?.Name == "CacheInvalidateAttribute" &&
               attr.ContainingNamespace?.ToDisplayString() == "MethodCache.Core";

        private static List<string> ExtractTags(AttributeData attr)
        {
            var tags = new List<string>();
            var tagsArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "Tags");
            if (!tagsArg.Value.Values.IsDefaultOrEmpty)
            {
                foreach (var value in tagsArg.Value.Values)
                {
                    if (value.Value?.ToString() is string tag && !string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag);
                }
            }
            return tags;
        }

        /// <summary>
        /// Parses dynamic tags with {parameterName} placeholders and returns both static and dynamic tag lists.
        /// </summary>
        private static List<(string template, List<string> paramNames)> ParseDynamicTags(List<string> tags, IMethodSymbol method)
        {
            var dynamicTags = new List<(string template, List<string> paramNames)>();
            var parameterNames = method.Parameters.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
            
            foreach (var tag in tags)
            {
                var paramNames = new List<string>();
                var template = tag;
                
                // Find all {parameterName} patterns in the tag
                var matches = DynamicTagParameterRegex.Matches(tag);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var paramName = match.Groups[1].Value;
                    if (parameterNames.ContainsKey(paramName))
                    {
                        paramNames.Add(paramName);
                        // Replace {paramName} with {0}, {1}, etc. for string.Format
                        template = template.Replace(match.Value, $"{{{paramNames.Count - 1}}}");
                    }
                    // Note: We can't emit diagnostics here because we don't have access to the context
                    // This will be handled in the validation phase
                }
                
                if (paramNames.Any())
                {
                    dynamicTags.Add((template, paramNames));
                }
            }
            
            return dynamicTags;
        }

        /// <summary>
        /// Validates dynamic tags and returns diagnostics for unknown parameters.
        /// </summary>
        private static List<Diagnostic> ValidateDynamicTags(List<string> tags, IMethodSymbol method)
        {
            var diagnostics = new List<Diagnostic>();
            var parameterNames = new HashSet<string>(method.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            
            foreach (var tag in tags)
            {
                var matches = DynamicTagParameterRegex.Matches(tag);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var paramName = match.Groups[1].Value;
                    if (!parameterNames.Contains(paramName))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.DynamicTagParameterNotFound,
                            method.Locations.FirstOrDefault(),
                            tag, paramName, method.ToDisplayString()));
                    }
                }
            }
            
            return diagnostics;
        }

        /// <summary>
        /// Emits code to build both static and dynamic tags for invalidation.
        /// </summary>
        private static void EmitTagInvalidation(StringBuilder sb, List<string> staticTags, List<(string template, List<string> paramNames)> dynamicTags, string indent)
        {
            sb.AppendLine($"{indent}var allTags = new List<string>();");
            
            // Add static tags
            if (staticTags.Any())
            {
                var staticTagsWithoutDynamic = staticTags.Where(t => !DynamicTagParameterRegex.IsMatch(t)).ToList();
                if (staticTagsWithoutDynamic.Any())
                {
                    sb.Append($"{indent}allTags.AddRange(new[] {{ ");
                    sb.Append(string.Join(", ", staticTagsWithoutDynamic.Select(t => $"\"{t}\"")));
                    sb.AppendLine(" });");
                }
            }
            
            // Add dynamic tags
            foreach (var (template, paramNames) in dynamicTags)
            {
                sb.Append($"{indent}allTags.Add(string.Format(\"{template}\"");
                foreach (var paramName in paramNames)
                {
                    sb.Append($", {paramName}?.ToString() ?? \"null\"");
                }
                sb.AppendLine("));");
            }
        }

        // ======================== Models ========================
        private sealed class InterfaceInfo
        {
            public INamedTypeSymbol Symbol { get; }
            public ImmutableArray<MethodModel> CachedMethods { get; }
            public ImmutableArray<MethodModel> InvalidateMethods { get; }
            public ImmutableArray<Diagnostic> Diagnostics { get; }

            public InterfaceInfo(INamedTypeSymbol symbol, ImmutableArray<MethodModel> cachedMethods, ImmutableArray<MethodModel> invalidateMethods, ImmutableArray<Diagnostic> diagnostics)
            {
                Symbol = symbol;
                CachedMethods = cachedMethods;
                InvalidateMethods = invalidateMethods;
                Diagnostics = diagnostics;
            }
        }

        private sealed class MethodModel
        {
            public IMethodSymbol Method { get; }
            public AttributeData? CacheAttr { get; }
            public AttributeData? InvalidateAttr { get; }

            public MethodModel(IMethodSymbol method, AttributeData? cacheAttr, AttributeData? invalidateAttr)
            {
                Method = method;
                CacheAttr = cacheAttr;
                InvalidateAttr = invalidateAttr;
            }
        }

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

        // ======================== Decorator Emitter ========================
        private static class DecoratorEmitter
        {
            internal static string Emit(InterfaceInfo info)
            {
                var sb = new StringBuilder();
                var ns = info.Symbol.ContainingNamespace.ToDisplayString();
                var interfaceFqn = Utils.GetFullyQualifiedName(info.Symbol);
                var className = $"{info.Symbol.Name}Decorator";
                
                static string GetSimpleInterfaceName(INamedTypeSymbol symbol)
                {
                    return symbol.ToDisplayString(new SymbolDisplayFormat(
                        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters));
                }


                // File header
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("#nullable enable");
                sb.AppendLine("#pragma warning disable CS8019 // Unnecessary using directive");
                sb.AppendLine("using System;");
                sb.AppendLine("using System.Collections.Generic;");
                sb.AppendLine("using System.Linq;");
                sb.AppendLine("using System.Threading;");
                sb.AppendLine("using System.Threading.Tasks;");
                sb.AppendLine("using MethodCache.Core;");
                sb.AppendLine("using MethodCache.Abstractions.Registry;");
                sb.AppendLine("using MethodCache.Core.Configuration;");
                sb.AppendLine("using MethodCache.Core.Configuration.Policies;");
                sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
                sb.AppendLine();
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");

                // Class declaration with generic parameters
                var genericParams = GetClassGenericParameters(info.Symbol);

                // Add XML documentation for generic interface limitations
                if (info.Symbol.IsGenericType)
                {
                    sb.AppendLine("    /// <summary>");
                    sb.AppendLine("    /// Cached implementation of " + GetSimpleInterfaceName(info.Symbol) + ".");
                    sb.AppendLine("    /// </summary>");
                    sb.AppendLine("    /// <remarks>");
                    sb.AppendLine("    /// This generic interface implementation provides type safety and convenience,");
                    sb.AppendLine("    /// but may have slight performance overhead compared to the non-generic interface");
                    sb.AppendLine("    /// for high-throughput scenarios due to runtime type resolution for cache keys.");
                    sb.AppendLine("    /// For maximum performance in critical paths, consider using non-generic interfaces.");
                    sb.AppendLine("    /// </remarks>");
                }

                sb.AppendLine("    [System.CodeDom.Compiler.GeneratedCode(\"MethodCacheGenerator\", \"1.0.0\")]");
                sb.AppendLine("    [System.Diagnostics.DebuggerNonUserCode]");
                sb.AppendLine($"    public class {className}{genericParams} : {GetSimpleInterfaceName(info.Symbol)}");
                
                // Add generic constraints after class declaration
                EmitGenericConstraints(sb, info.Symbol, "        ");
                
                sb.AppendLine("    {");

                // Fields
                sb.AppendLine($"        private readonly {interfaceFqn} _decorated;");
                sb.AppendLine("        private readonly ICacheManager _cacheManager;");
                sb.AppendLine("        private readonly IPolicyRegistry _policyRegistry;");
                sb.AppendLine("        private readonly ICacheKeyGenerator _keyGenerator;");
                sb.AppendLine();

                // Constructor
                EmitConstructor(sb, className, interfaceFqn, info.Symbol);

                // Methods
                var allMethods = info.Symbol.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Concat(info.Symbol.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>()))
                    .Distinct(SymbolEqualityComparer.Default)
                    .Cast<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary);

                foreach (var method in allMethods)
                {
                    var cached = info.CachedMethods.FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(m.Method, method));
                    var invalidate = info.InvalidateMethods.FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(m.Method, method));

                    if (cached != null)
                        EmitCachedMethod(sb, cached, info.Symbol);
                    else if (invalidate != null)
                        EmitInvalidateMethod(sb, invalidate);
                    else
                        EmitPassthroughMethod(sb, method);
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine("#pragma warning restore CS8019");

                return sb.ToString();
            }

            private static string GetClassGenericParameters(INamedTypeSymbol symbol)
            {
                if (!symbol.IsGenericType || symbol.TypeParameters.Length == 0)
                    return string.Empty;
                
                return $"<{string.Join(", ", symbol.TypeParameters.Select(tp => tp.Name))}>";
            }

            private static void EmitGenericConstraints(StringBuilder sb, INamedTypeSymbol symbol, string indent)
            {
                if (!symbol.IsGenericType || symbol.TypeParameters.Length == 0)
                    return;

                foreach (var typeParam in symbol.TypeParameters)
                {
                    var constraints = new List<string>();

                    // Reference type constraint
                    if (typeParam.HasReferenceTypeConstraint)
                        constraints.Add("class");

                    // Value type constraint
                    if (typeParam.HasValueTypeConstraint)
                        constraints.Add("struct");

                    // Notnull constraint (C# 8.0+)
                    if (typeParam.HasNotNullConstraint)
                        constraints.Add("notnull");

                    // Unmanaged constraint (C# 7.3+)
                    if (typeParam.HasUnmanagedTypeConstraint)
                        constraints.Add("unmanaged");

                    // Specific base types or interfaces
                    foreach (var constraintType in typeParam.ConstraintTypes)
                    {
                        constraints.Add(constraintType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                    }

                    // Constructor constraint
                    if (typeParam.HasConstructorConstraint)
                        constraints.Add("new()");

                    if (constraints.Count > 0)
                    {
                        sb.Append(indent);
                        sb.AppendLine($"where {typeParam.Name} : {string.Join(", ", constraints)}");
                    }
                }
            }

            private static void EmitConstructor(StringBuilder sb, string className, string interfaceFqn, INamedTypeSymbol interfaceSymbol)
            {
                // Constructor name should NOT include generic parameters - that's invalid C# syntax
                sb.AppendLine($"        public {className}(");
                sb.AppendLine($"            {interfaceFqn} decorated,");
                sb.AppendLine("            ICacheManager cacheManager,");
                sb.AppendLine("            IPolicyRegistry policyRegistry,");
                sb.AppendLine("            IServiceProvider serviceProvider)");
                sb.AppendLine("        {");
                sb.AppendLine("            _decorated = decorated ?? throw new ArgumentNullException(nameof(decorated));");
                sb.AppendLine("            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));");
                sb.AppendLine("            _policyRegistry = policyRegistry ?? throw new ArgumentNullException(nameof(policyRegistry));");
                sb.AppendLine("            if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));");
                sb.AppendLine("            _keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            private static void EmitCachedMethod(StringBuilder sb, MethodModel model, INamedTypeSymbol interfaceSymbol)
            {
                var method = model.Method;
                var returnType = Utils.GetReturnTypeForSignature(method.ReturnType);
                var methodId = Utils.GetMethodId(method);

                // Method signature
                EmitMethodSignature(sb, method);
                sb.AppendLine("        {");

                // Get cache configuration
                // Build cache key parameters (exclude CancellationToken)
                var keyParams = method.Parameters
                    .Where(p => !Utils.IsCancellationToken(p.Type))
                    .ToList();

                // Generate cache key array
                if (keyParams.Any())
                {
                    sb.Append("            var args = new object[] { ");
                    sb.Append(string.Join(", ", keyParams.Select(p => p.Name)));
                    sb.AppendLine(" };");
                }
                else
                {
                    sb.AppendLine("            var args = new object[] { };");
                }

                sb.AppendLine($"            var policyResult = _policyRegistry.GetPolicy(\"{methodId}\");");
                sb.AppendLine("            var settings = CachePolicyConversion.ToCacheMethodSettings(policyResult.Policy);");

                // Handle different return types
                if (Utils.IsTask(method.ReturnType, out var taskType))
                {
                    EmitTaskCaching(sb, method, taskType!, interfaceSymbol);
                }
                else if (Utils.IsValueTask(method.ReturnType, out var valueTaskType))
                {
                    EmitValueTaskCaching(sb, method, valueTaskType!, interfaceSymbol);
                }
                else
                {
                    EmitSyncCaching(sb, method, returnType, interfaceSymbol);
                }

                sb.AppendLine("        }");
                sb.AppendLine();
            }

            private const string IsIdempotentProperty = "settings.IsIdempotent";

            /// <summary>
            /// Generates cache method name that includes runtime generic type information for generic interfaces
            /// </summary>
            private static string GetCacheMethodName(IMethodSymbol method, INamedTypeSymbol interfaceSymbol)
            {
                // For generic interfaces, we'll generate the name dynamically at runtime 
                // This will be handled in the emit methods by generating appropriate code
                return method.Name; // This will be replaced with dynamic code in emit methods
            }

            private static void EmitTaskCaching(StringBuilder sb, IMethodSymbol method, string innerType, INamedTypeSymbol interfaceSymbol)
            {
                var call = BuildMethodCall(method);
                
                // Generate dynamic cache method name for generic interfaces
                if (interfaceSymbol.IsGenericType)
                {
                    var baseInterfaceName = interfaceSymbol.Name;
                    var methodName = method.Name;
                    sb.AppendLine($"            var cacheMethodName = \"{baseInterfaceName}<\" + string.Join(\",\", this.GetType().GetGenericArguments().Select(t => t.Name)) + \">.{methodName}\";");
                    sb.AppendLine($"            return _cacheManager.GetOrCreateAsync<{innerType}>(");
                    sb.AppendLine("                cacheMethodName,");
                }
                else
                {
                    sb.AppendLine($"            return _cacheManager.GetOrCreateAsync<{innerType}>(");
                    sb.AppendLine($"                \"{method.Name}\",");
                }
                sb.AppendLine("                args,");
                sb.AppendLine($"                async () => await {call}.ConfigureAwait(false),");
                sb.AppendLine("                settings,");
                sb.AppendLine("                _keyGenerator,");
                sb.AppendLine($"                {IsIdempotentProperty});");
            }

            private static void EmitValueTaskCaching(StringBuilder sb, IMethodSymbol method, string innerType, INamedTypeSymbol interfaceSymbol)
            {
                var call = BuildMethodCall(method);
                
                // Generate dynamic cache method name for generic interfaces
                if (interfaceSymbol.IsGenericType)
                {
                    var baseInterfaceName = interfaceSymbol.Name;
                    var methodName = method.Name;
                    sb.AppendLine($"            var cacheMethodName = \"{baseInterfaceName}<\" + string.Join(\",\", this.GetType().GetGenericArguments().Select(t => t.Name)) + \">.{methodName}\";");
                    sb.AppendLine($"            var task = _cacheManager.GetOrCreateAsync<{innerType}>(");
                    sb.AppendLine("                cacheMethodName,");
                }
                else
                {
                    sb.AppendLine($"            var task = _cacheManager.GetOrCreateAsync<{innerType}>(");
                    sb.AppendLine($"                \"{method.Name}\",");
                }
                sb.AppendLine("                args,");
                sb.AppendLine($"                async () => await {call}.AsTask().ConfigureAwait(false),");
                sb.AppendLine("                settings,");
                sb.AppendLine("                _keyGenerator,");
                sb.AppendLine($"                {IsIdempotentProperty});");
                sb.AppendLine($"            return new ValueTask<{innerType}>(task);");
            }

            private static void EmitSyncCaching(StringBuilder sb, IMethodSymbol method, string returnType, INamedTypeSymbol interfaceSymbol)
            {
                var call = BuildMethodCall(method);

                // Add warning comment about sync-over-async
                sb.AppendLine("            // WARNING: This is a sync-over-async pattern that may cause deadlocks");
                sb.AppendLine("            // in environments with SynchronizationContext (ASP.NET Framework, WPF, WinForms).");
                sb.AppendLine("            // Consider making the method async to avoid potential issues.");

                // Generate dynamic cache method name for generic interfaces
                if (interfaceSymbol.IsGenericType)
                {
                    var baseInterfaceName = interfaceSymbol.Name;
                    var methodName = method.Name;
                    sb.AppendLine($"            var cacheMethodName = \"{baseInterfaceName}<\" + string.Join(\",\", this.GetType().GetGenericArguments().Select(t => t.Name)) + \">.{methodName}\";");
                    sb.AppendLine($"            return _cacheManager.GetOrCreateAsync<{returnType}>(");
                    sb.AppendLine("                cacheMethodName,");
                }
                else
                {
                    sb.AppendLine($"            return _cacheManager.GetOrCreateAsync<{returnType}>(");
                    sb.AppendLine($"                \"{method.Name}\",");
                }
                sb.AppendLine("                args,");
                sb.AppendLine($"                () => Task.FromResult({call}),");
                sb.AppendLine("                settings,");
                sb.AppendLine("                _keyGenerator,");
                sb.AppendLine($"                {IsIdempotentProperty})");
                sb.AppendLine("                .ConfigureAwait(false).GetAwaiter().GetResult();");
            }

            private static void EmitInvalidateMethod(StringBuilder sb, MethodModel model)
            {
                var method = model.Method;
                var tags = ExtractTags(model.InvalidateAttr!);
                var dynamicTags = ParseDynamicTags(tags, method);
                
                // For async methods, we need to emit async method signature
                if (Utils.IsTask(method.ReturnType, out _) || Utils.IsValueTask(method.ReturnType, out _))
                {
                    EmitAsyncInvalidateMethodSignature(sb, method);
                }
                else
                {
                    EmitMethodSignature(sb, method);
                }
                
                sb.AppendLine("        {");

                var call = BuildMethodCall(method);
                
                // Handle async methods differently
                if (Utils.IsTask(method.ReturnType, out _) || Utils.IsValueTask(method.ReturnType, out _))
                {
                    // For async methods, emit async wrapper
                    EmitAsyncInvalidateMethodBody(sb, method, call, tags, dynamicTags);
                }
                else
                {
                    // For sync methods, emit sync wrapper
                    EmitSyncInvalidateMethodBody(sb, method, call, tags, dynamicTags);
                }

                sb.AppendLine("        }");
                sb.AppendLine();
            }

            private static void EmitAsyncInvalidateMethodSignature(StringBuilder sb, IMethodSymbol method)
            {
                var returnType = Utils.GetReturnTypeForSignature(method.ReturnType);
                var typeParams = method.TypeParameters.Any()
                    ? $"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>"
                    : string.Empty;

                var parameters = string.Join(", ", method.Parameters.Select(p =>
                {
                    var modifier = p.RefKind switch
                    {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In => "in ",
                        _ => ""
                    };
                    var defaultValue = p.HasExplicitDefaultValue ? $" = {FormatDefaultValue(p.ExplicitDefaultValue)}" : "";
                    return $"{modifier}{Utils.GetReturnTypeForSignature(p.Type)} {p.Name}{defaultValue}";
                }));

                sb.AppendLine($"        public async {returnType} {method.Name}{typeParams}({parameters})");

                // Add generic constraints if any
                foreach (var tp in method.TypeParameters)
                {
                    var constraints = BuildConstraints(tp);
                    if (!string.IsNullOrEmpty(constraints))
                        sb.AppendLine($"            {constraints}");
                }
            }

            private static void EmitAsyncInvalidateMethodBody(StringBuilder sb, IMethodSymbol method, string call, List<string> staticTags, List<(string template, List<string> paramNames)> dynamicTags)
            {
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                
                if (method.ReturnsVoid || (method.ReturnType is INamedTypeSymbol nt && nt.Name == "Task" && !nt.IsGenericType))
                {
                    sb.AppendLine($"                await {call}.ConfigureAwait(false);");
                }
                else
                {
                    sb.AppendLine($"                var result = await {call}.ConfigureAwait(false);");
                }
                
                // Invalidate cache AFTER successful execution
                if (staticTags.Any() || dynamicTags.Any())
                {
                    EmitTagInvalidation(sb, staticTags, dynamicTags, "                ");
                    sb.AppendLine("                await _cacheManager.InvalidateByTagsAsync(allTags.ToArray()).ConfigureAwait(false);");
                }
                
                if (!method.ReturnsVoid && !(method.ReturnType is INamedTypeSymbol nt2 && nt2.Name == "Task" && !nt2.IsGenericType))
                {
                    sb.AppendLine("                return result;");
                }
                
                sb.AppendLine("            }");
                sb.AppendLine("            catch");
                sb.AppendLine("            {");
                sb.AppendLine("                // Don't invalidate cache on failure");
                sb.AppendLine("                throw;");
                sb.AppendLine("            }");
            }

            private static void EmitSyncInvalidateMethodBody(StringBuilder sb, IMethodSymbol method, string call, List<string> staticTags, List<(string template, List<string> paramNames)> dynamicTags)
            {
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                
                if (method.ReturnsVoid)
                {
                    sb.AppendLine($"                {call};");
                }
                else
                {
                    sb.AppendLine($"                var result = {call};");
                }
                
                // Invalidate cache AFTER successful execution
                if (staticTags.Any() || dynamicTags.Any())
                {
                    EmitTagInvalidation(sb, staticTags, dynamicTags, "                ");
                    sb.AppendLine("                _cacheManager.InvalidateByTagsAsync(allTags.ToArray()).GetAwaiter().GetResult();");
                }
                
                if (!method.ReturnsVoid)
                {
                    sb.AppendLine("                return result;");
                }
                
                sb.AppendLine("            }");
                sb.AppendLine("            catch");
                sb.AppendLine("            {");
                sb.AppendLine("                // Don't invalidate cache on failure");
                sb.AppendLine("                throw;");
                sb.AppendLine("            }");
            }

            private static void EmitPassthroughMethod(StringBuilder sb, IMethodSymbol method)
            {
                EmitMethodSignature(sb, method);
                sb.AppendLine("        {");

                var call = BuildMethodCall(method);
                if (method.ReturnsVoid)
                    sb.AppendLine($"            {call};");
                else
                    sb.AppendLine($"            return {call};");

                sb.AppendLine("        }");
                sb.AppendLine();
            }


            private static void EmitMethodSignature(StringBuilder sb, IMethodSymbol method)
            {
                var returnType = Utils.GetReturnTypeForSignature(method.ReturnType);
                var typeParams = method.TypeParameters.Any()
                    ? $"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>"
                    : string.Empty;

                var parameters = string.Join(", ", method.Parameters.Select(p =>
                {
                    var modifier = p.RefKind switch
                    {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In => "in ",
                        _ => ""
                    };
                    var defaultValue = p.HasExplicitDefaultValue ? $" = {FormatDefaultValue(p.ExplicitDefaultValue)}" : "";
                    return $"{modifier}{Utils.GetReturnTypeForSignature(p.Type)} {p.Name}{defaultValue}";
                }));

                sb.AppendLine($"        public {returnType} {method.Name}{typeParams}({parameters})");

                // Add generic constraints if any
                foreach (var tp in method.TypeParameters)
                {
                    var constraints = BuildConstraints(tp);
                    if (!string.IsNullOrEmpty(constraints))
                        sb.AppendLine($"            {constraints}");
                }
            }

            private static string BuildMethodCall(IMethodSymbol method)
            {
                var typeArgs = method.TypeParameters.Any()
                    ? $"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>"
                    : string.Empty;

                var args = string.Join(", ", method.Parameters.Select(p =>
                {
                    var modifier = p.RefKind switch
                    {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In => "in ",
                        _ => ""
                    };
                    return $"{modifier}{p.Name}";
                }));

                return $"_decorated.{method.Name}{typeArgs}({args})";
            }

            private static string BuildConstraints(ITypeParameterSymbol tp)
            {
                var constraints = new List<string>();

                if (tp.HasReferenceTypeConstraint)
                    constraints.Add("class");
                if (tp.HasValueTypeConstraint)
                    constraints.Add("struct");
                if (tp.HasUnmanagedTypeConstraint)
                    constraints.Add("unmanaged");
                if (tp.HasNotNullConstraint)
                    constraints.Add("notnull");

                constraints.AddRange(tp.ConstraintTypes.Select(ct => Utils.GetFullyQualifiedName(ct)));

                if (tp.HasConstructorConstraint && !tp.HasValueTypeConstraint && !tp.HasUnmanagedTypeConstraint)
                    constraints.Add("new()");

                return constraints.Any()
                    ? $"where {tp.Name} : {string.Join(", ", constraints)}"
                    : string.Empty;
            }

            private static string FormatDefaultValue(object? value)
            {
                return value switch
                {
                    null => "null",
                    string s => $"\"{s}\"",
                    char c => $"'{c}'",
                    bool b => b.ToString().ToLowerInvariant(),
                    _ => value.ToString() ?? "default"
                };
            }
        }

        // ======================== Registry Emitter ========================
        private static class RegistryEmitter
        {
            private static string GetSimpleTypeName(ITypeSymbol type)
            {
                return type.ToDisplayString(new SymbolDisplayFormat(
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters));
            }

            private static string GetSimpleParameterType(ITypeSymbol type)
            {
                // Use the same logic as Utils.GetReturnTypeForSignature for consistency
                return Utils.GetReturnTypeForSignature(type);
            }

            private static List<string> BuildConfigureStatements(AttributeData? cacheAttr)
            {
                var statements = new List<string>();

                if (cacheAttr == null)
                {
                    return statements;
                }

                if (TryGetNamedArgument(cacheAttr, "Duration", out var durationArg) &&
                    durationArg.Value is string duration && !string.IsNullOrWhiteSpace(duration))
                {
                    statements.Add($"options.WithDuration(System.TimeSpan.Parse(\"{duration}\"));");
                }

                if (TryGetNamedArgument(cacheAttr, "Tags", out var tagsArg) && tagsArg.Kind == TypedConstantKind.Array)
                {
                    var tagValues = tagsArg.Values
                        .Select(v => v.Value as string)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => $"\"{s}\"")
                        .ToArray();

                    if (tagValues.Length > 0)
                    {
                        statements.Add($"options.WithTags({string.Join(", ", tagValues)});");
                    }
                }

                if (TryGetNamedArgument(cacheAttr, "Version", out var versionArg) &&
                    versionArg.Value is int versionValue && versionValue >= 0)
                {
                    statements.Add($"options.WithVersion({versionValue});");
                }

                if (TryGetNamedArgument(cacheAttr, "KeyGeneratorType", out var keyGenArg) &&
                    keyGenArg.Value is INamedTypeSymbol keyGenType)
                {
                    var generatorName = GetSimpleTypeName(keyGenType);
                    statements.Add($"options.WithKeyGenerator<{generatorName}>();");
                }

                return statements;
            }

            private static bool TryGetNamedArgument(AttributeData attribute, string name, out TypedConstant value)
            {
                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.Key == name)
                    {
                        value = argument.Value;
                        return true;
                    }
                }

                value = default;
                return false;
            }

            internal static string Emit(List<InterfaceInfo> interfaces)
            {
                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("#nullable enable");
                sb.AppendLine("using System.Runtime.CompilerServices;");
                sb.AppendLine("using MethodCache.Core;");
                sb.AppendLine("using MethodCache.Core.Configuration;");
                sb.AppendLine("using MethodCache.Core.Configuration.Fluent;");
                sb.AppendLine();
                sb.AppendLine("namespace MethodCache.Core");
                sb.AppendLine("{");
                sb.AppendLine("    internal class GeneratedCacheMethodRegistry : ICacheMethodRegistry");
                sb.AppendLine("    {");
                sb.AppendLine("        public void RegisterMethods(IMethodCacheConfiguration config)");
                sb.AppendLine("        {");

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var cachedInterfaces = interfaces.Where(i => i.CachedMethods.Length > 0 && !i.Symbol.IsGenericType).ToList();

                if (cachedInterfaces.Count > 0)
                {
                    sb.AppendLine("            config.ApplyFluent(fluent =>");
                    sb.AppendLine("            {");

                    foreach (var info in cachedInterfaces)
                    {
                        var interfaceName = GetSimpleTypeName(info.Symbol);

                        foreach (var model in info.CachedMethods)
                        {
                            // NOTE: Generic methods are currently not supported by the source generator.
                            // This is due to runtime type parameter resolution limitations - we cannot
                            // generate cache keys for generic type parameters that are only known at runtime.
                            //
                            // Potential alternatives:
                            // 1. Use non-generic wrapper methods that call generic implementations
                            // 2. Implement runtime key generation (performance impact)
                            // 3. Use manual caching with ICacheManager for generic scenarios
                            //
                            // Example workaround:
                            // [Cache] public Task<T> GetData<T>(int id) => GetDataInternal<T>(id);
                            // private Task<T> GetDataInternal<T>(int id) { /* implementation */ }
                            if (model.Method.IsGenericMethod)
                            {
                                // Skip generic methods - see documentation above
                                continue;
                            }
                            
                            var methodId = Utils.GetMethodId(model.Method);
                            if (!seen.Add(methodId))
                            {
                                continue;
                            }

                            var paramList = string.Join(", ", model.Method.Parameters.Select(p => $"Any<{GetSimpleParameterType(p.Type)}>.Value"));
                            var configureStatements = BuildConfigureStatements(model.CacheAttr);
                            var hasConfigure = configureStatements.Count > 0;

                            var groupArg = model.CacheAttr?.NamedArguments
                                .FirstOrDefault(a => a.Key == "GroupName").Value.Value as string;
                            var hasGroup = !string.IsNullOrWhiteSpace(groupArg);

                            var requireIdempotent = false;
                            if (model.CacheAttr != null &&
                                TryGetNamedArgument(model.CacheAttr, "RequireIdempotent", out var idempotentArg) &&
                                idempotentArg.Value is bool b && b)
                            {
                                requireIdempotent = true;
                            }

                            sb.AppendLine($"                fluent.ForService<{interfaceName}>()");

                            var methodLine = $"                    .Method(x => x.{model.Method.Name}({paramList}))";
                            var trailingCalls = new List<string>();
                            if (hasGroup)
                            {
                                trailingCalls.Add($".WithGroup(\"{groupArg}\")");
                            }
                            if (requireIdempotent)
                            {
                                trailingCalls.Add(".RequireIdempotent(true)");
                            }

                            var hasAdditional = hasConfigure || trailingCalls.Count > 0;

                            if (!hasAdditional)
                            {
                                sb.AppendLine(methodLine + ";");
                                continue;
                            }

                            sb.AppendLine(methodLine);

                            if (hasConfigure)
                            {
                                sb.AppendLine("                    .Configure(options =>");
                                sb.AppendLine("                    {");
                                foreach (var statement in configureStatements)
                                {
                                    sb.AppendLine($"                        {statement}");
                                }
                                sb.AppendLine(trailingCalls.Count > 0 ? "                    })" : "                    });");
                            }

                            for (var i = 0; i < trailingCalls.Count; i++)
                            {
                                var call = trailingCalls[i];
                                var suffix = i == trailingCalls.Count - 1 ? ";" : string.Empty;
                                sb.AppendLine($"                    {call}{suffix}");
                            }
                        }
                    }

                    sb.AppendLine("            });");
                }

                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    internal static class ModuleInitializer");
                sb.AppendLine("    {");
                sb.AppendLine("        [ModuleInitializer]");
                sb.AppendLine("        internal static void Initialize()");
                sb.AppendLine("        {");
                sb.AppendLine("            CacheMethodRegistry.SetRegistry(new GeneratedCacheMethodRegistry());");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine("}");

                return sb.ToString();
            }
        }

        // ======================== DI Extensions Emitter ========================
        private static class DIExtensionsEmitter
        {
            internal static string Emit(List<InterfaceInfo> interfaces)
            {
                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("#nullable enable");
                sb.AppendLine("using System;");
                sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
                sb.AppendLine("using MethodCache.Core;");
                sb.AppendLine("using MethodCache.Core.Configuration;");
                sb.AppendLine("using MethodCache.Abstractions.Registry;");
                sb.AppendLine("using MethodCache.Core.Configuration.Policies;");
                sb.AppendLine();
                sb.AppendLine("namespace Microsoft.Extensions.DependencyInjection");
                sb.AppendLine("{");
                sb.AppendLine("    public static class MethodCacheServiceCollectionExtensions");
                sb.AppendLine("    {");

                foreach (var info in interfaces.Where(i => !i.Symbol.IsGenericType))
                {
                    EmitInterfaceExtensions(sb, info);
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");

                return sb.ToString();
            }

            private static void EmitInterfaceExtensions(StringBuilder sb, InterfaceInfo info)
            {
                var interfaceFqn = Utils.GetFullyQualifiedName(info.Symbol);
                var decoratorFqn = $"{info.Symbol.ContainingNamespace.ToDisplayString()}.{info.Symbol.Name}Decorator";
                var baseName = $"{info.Symbol.Name}WithCaching";

                // Default (Singleton for backward compatibility)
                sb.AppendLine($"        public static IServiceCollection Add{baseName}(");
                sb.AppendLine($"            this IServiceCollection services,");
                sb.AppendLine($"            Func<IServiceProvider, {interfaceFqn}> implementationFactory)");
                sb.AppendLine("        {");
                sb.AppendLine($"            return services.AddSingleton<{interfaceFqn}>(sp =>");
                sb.AppendLine($"                new {decoratorFqn}(");
                sb.AppendLine("                    implementationFactory(sp),");
                sb.AppendLine("                    sp.GetRequiredService<ICacheManager>(),");
                sb.AppendLine("                    sp.GetRequiredService<IPolicyRegistry>(),");
                sb.AppendLine("                    sp));");
                sb.AppendLine("        }");
                sb.AppendLine();

                // Explicit lifetime methods
                var lifetimes = new[] { ("Singleton", "AddSingleton"), ("Scoped", "AddScoped"), ("Transient", "AddTransient") };
                foreach (var (suffix, method) in lifetimes)
                {
                    sb.AppendLine($"        public static IServiceCollection Add{baseName}{suffix}(");
                    sb.AppendLine($"            this IServiceCollection services,");
                    sb.AppendLine($"            Func<IServiceProvider, {interfaceFqn}> implementationFactory)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            return services.{method}<{interfaceFqn}>(sp =>");
                    sb.AppendLine($"                new {decoratorFqn}(");
                    sb.AppendLine("                    implementationFactory(sp),");
                    sb.AppendLine("                    sp.GetRequiredService<ICacheManager>(),");
                    sb.AppendLine("                    sp.GetRequiredService<IPolicyRegistry>(),");
                    sb.AppendLine("                    sp));");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }
        }
    }
}
