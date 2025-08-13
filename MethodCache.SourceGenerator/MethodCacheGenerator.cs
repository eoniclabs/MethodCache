using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace MethodCache.SourceGenerator
{
    [Generator]
    public class MethodCacheGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var interfaceProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsSyntaxTargetForGeneration(s),
                    transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
                .Where(static m => m is not null);

            context.RegisterSourceOutput(interfaceProvider.Collect(), Execute);
        }

        private static bool IsSyntaxTargetForGeneration(SyntaxNode node)
        {
            return node is InterfaceDeclarationSyntax interfaceDecl &&
                   interfaceDecl.Members.OfType<MethodDeclarationSyntax>().Any();
        }

        private static InterfaceInfo? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
        {
            var interfaceDecl = (InterfaceDeclarationSyntax)context.Node;
            var interfaceSymbol = context.SemanticModel.GetDeclaredSymbol(interfaceDecl) as INamedTypeSymbol;

            if (interfaceSymbol == null) return null;

            var cachedMethods = new List<IMethodSymbol>();
            var invalidateMethods = new List<IMethodSymbol>();

            foreach (var member in interfaceSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                var attributes = member.GetAttributes();
                
                // Use string-based detection to avoid assembly references
                if (attributes.Any(ad => IsCacheAttribute(ad.AttributeClass)))
                {
                    cachedMethods.Add(member);
                }
                
                if (attributes.Any(ad => IsCacheInvalidateAttribute(ad.AttributeClass)))
                {
                    invalidateMethods.Add(member);
                }
            }

            if (cachedMethods.Any() || invalidateMethods.Any())
            {
                return new InterfaceInfo(interfaceSymbol, cachedMethods, invalidateMethods);
            }

            return null;
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

        private static void Execute(SourceProductionContext context, ImmutableArray<InterfaceInfo?> interfaces)
        {
            var validInterfaces = interfaces.Where(i => i != null).Cast<InterfaceInfo>().ToList();

            var allCachedMethods = validInterfaces.SelectMany(i => i.CachedMethods).ToList();

            // Generate the registry only if there are cached methods
            if (allCachedMethods.Any())
            {
                var registryCode = GenerateRegistry(allCachedMethods);
                context.AddSource("CacheMethodRegistry.g.cs", Microsoft.CodeAnalysis.Text.SourceText.From(registryCode.NormalizeWhitespace().ToFullString(), Encoding.UTF8));
            }

            // Generate service registration extensions if we have interfaces to decorate
            if (validInterfaces.Any())
            {
                var serviceRegistrationCode = GenerateServiceRegistrationExtensions(validInterfaces);
                context.AddSource("MethodCacheServiceCollectionExtensions.g.cs",
                    Microsoft.CodeAnalysis.Text.SourceText.From(serviceRegistrationCode.NormalizeWhitespace().ToFullString(), Encoding.UTF8));
            }

            foreach (var interfaceInfo in validInterfaces)
            {
                var decoratorCode = GenerateDecoratorClass(interfaceInfo.InterfaceSymbol);
                context.AddSource($@"{interfaceInfo.InterfaceSymbol.Name}Decorator.g.cs",
                    Microsoft.CodeAnalysis.Text.SourceText.From(decoratorCode.NormalizeWhitespace().ToFullString(), Encoding.UTF8));
            }
        }

        private class InterfaceInfo
        {
            public INamedTypeSymbol InterfaceSymbol { get; }
            public List<IMethodSymbol> CachedMethods { get; }
            public List<IMethodSymbol> InvalidateMethods { get; }

            public InterfaceInfo(INamedTypeSymbol interfaceSymbol, List<IMethodSymbol> cachedMethods, List<IMethodSymbol> invalidateMethods)
            {
                InterfaceSymbol = interfaceSymbol;
                CachedMethods = cachedMethods;
                InvalidateMethods = invalidateMethods;
            }
        }

        private static CompilationUnitSyntax GenerateRegistry(List<IMethodSymbol> methods)
        {
            var registerMethodsStatements = new List<StatementSyntax>();

            foreach (var method in methods)
            {
                var containingType = method.ContainingType.ToDisplayString();
                var methodName = method.Name;

                var attribute = method.GetAttributes().FirstOrDefault(ad => IsCacheAttribute(ad.AttributeClass));
                var groupName = attribute?.ConstructorArguments.FirstOrDefault().Value?.ToString();

                var parameterArguments = method.Parameters.Select(p =>
                    Argument(MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        GenericName(Identifier("Any"))
                            .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList<TypeSyntax>(ParseTypeName(p.Type.ToDisplayString())))),
                        IdentifierName("Value")))).ToArray();
                var methodId = $"{containingType}.{methodName}";

                var groupNameArgument = groupName != null 
                    ? LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(groupName))
                    : LiteralExpression(SyntaxKind.NullLiteralExpression);

                registerMethodsStatements.Add(
                    ExpressionStatement(
                        InvocationExpression(
                            MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                IdentifierName("config"),
                                GenericName(Identifier("RegisterMethod"))
                                    .WithTypeArgumentList(
                                        TypeArgumentList(
                                            SingletonSeparatedList<TypeSyntax>(
                                                IdentifierName(containingType))))))
                            .WithArgumentList(
                                ArgumentList(
                                    SeparatedList<ArgumentSyntax>(
                                        new SyntaxNodeOrToken[]
                                        {
                                            Argument(
                                                SimpleLambdaExpression(
                                                    Parameter(Identifier("x")))
                                                    .WithExpressionBody(
                                                        InvocationExpression(
                                                            MemberAccessExpression(
                                                                SyntaxKind.SimpleMemberAccessExpression,
                                                                IdentifierName("x"),
                                                                IdentifierName(methodName)))
                                                            .WithArgumentList(
                                                                ArgumentList(
                                                                    SeparatedList(parameterArguments))))),
                                            Token(SyntaxKind.CommaToken),
                                            Argument(
                                                LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(methodId))),
                                            Token(SyntaxKind.CommaToken),
                                            Argument(groupNameArgument)
                                        })))));
            }

            // Create the registry implementation class
            var registryClass = ClassDeclaration("GeneratedCacheMethodRegistry")
                .AddModifiers(Token(SyntaxKind.InternalKeyword))
                .AddBaseListTypes(SimpleBaseType(IdentifierName("ICacheMethodRegistry")))
                .AddMembers(
                    MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "RegisterMethods")
                        .AddModifiers(Token(SyntaxKind.PublicKeyword))
                        .AddParameterListParameters(
                            Parameter(Identifier("config"))
                                .WithType(IdentifierName("IMethodCacheConfiguration")))
                        .WithBody(Block(registerMethodsStatements)));

            // Create the module initializer to register the implementation
            var moduleInitializer = ClassDeclaration("ModuleInitializer")
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword))
                .AddMembers(
                    MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Initialize")
                        .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword))
                        .AddAttributeLists(AttributeList(SingletonSeparatedList(
                            Attribute(IdentifierName("System.Runtime.CompilerServices.ModuleInitializer")))))
                        .WithBody(Block(
                            ExpressionStatement(
                                InvocationExpression(
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("CacheMethodRegistry"),
                                        IdentifierName("SetRegistry")))
                                    .WithArgumentList(ArgumentList(SingletonSeparatedList(
                                        Argument(ObjectCreationExpression(IdentifierName("GeneratedCacheMethodRegistry"))
                                            .WithArgumentList(ArgumentList())))))))));

            return CompilationUnit()
                .AddUsings(
                    UsingDirective(ParseName("System")),
                    UsingDirective(ParseName("System.Linq.Expressions")),
                    UsingDirective(ParseName("MethodCache.Core")),
                    UsingDirective(ParseName("MethodCache.Core.Configuration")))
                .AddMembers(
                    NamespaceDeclaration(ParseName("MethodCache.Core"))
                        .AddMembers(registryClass, moduleInitializer));
        }

        private static CompilationUnitSyntax GenerateServiceRegistrationExtensions(List<InterfaceInfo> interfaces)
        {
            var registrationMethods = new List<MemberDeclarationSyntax>();

            foreach (var interfaceInfo in interfaces)
            {
                var interfaceType = interfaceInfo.InterfaceSymbol;
                var interfaceName = interfaceType.Name;
                var fullInterfaceName = interfaceType.ToDisplayString();
                var decoratorName = $"{interfaceName}Decorator";

                // Create a simple helper method that avoids complex nested syntax
                var methodBody = $@"
            return services.AddSingleton<{fullInterfaceName}>(provider =>
                new {interfaceType.ContainingNamespace.ToDisplayString()}.{decoratorName}(
                    implementationFactory(provider),
                    provider.GetRequiredService<ICacheManager>(),
                    provider.GetRequiredService<MethodCacheConfiguration>(),
                    provider));";

                var helperMethod = MethodDeclaration(IdentifierName("IServiceCollection"), $"Add{interfaceName}WithCaching")
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        Parameter(Identifier("services"))
                            .WithModifiers(TokenList(Token(SyntaxKind.ThisKeyword)))
                            .WithType(IdentifierName("IServiceCollection")),
                        Parameter(Identifier("implementationFactory"))
                            .WithType(GenericName(Identifier("Func"))
                                .WithTypeArgumentList(TypeArgumentList(SeparatedList(new TypeSyntax[]
                                {
                                    IdentifierName("IServiceProvider"),
                                    IdentifierName(fullInterfaceName)
                                })))))
                    .WithBody(Block(ParseStatement(methodBody)));

                registrationMethods.Add(helperMethod);
            }

            return CompilationUnit()
                .AddUsings(
                    UsingDirective(ParseName("System")),
                    UsingDirective(ParseName("Microsoft.Extensions.DependencyInjection")),
                    UsingDirective(ParseName("MethodCache.Core")),
                    UsingDirective(ParseName("MethodCache.Core.Configuration")))
                .AddMembers(
                    NamespaceDeclaration(ParseName("Microsoft.Extensions.DependencyInjection"))
                        .AddMembers(
                            ClassDeclaration("MethodCacheServiceCollectionExtensions")
                                .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
                                .AddMembers(registrationMethods.ToArray())));
        }

        private static CompilationUnitSyntax GenerateDecoratorClass(INamedTypeSymbol interfaceSymbol)
        {
            var decoratorClassName = $"{interfaceSymbol.Name}Decorator";
            var decoratedInterfaceName = interfaceSymbol.ToDisplayString();
            var namespaceName = interfaceSymbol.ContainingNamespace.ToDisplayString();

            var fields = new List<MemberDeclarationSyntax>
            {
                FieldDeclaration(
                    VariableDeclaration(ParseTypeName(decoratedInterfaceName))
                        .AddVariables(VariableDeclarator(Identifier("_decorated"))))
                    .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)),
                FieldDeclaration(
                    VariableDeclaration(ParseTypeName("ICacheManager"))
                        .AddVariables(VariableDeclarator(Identifier("_cacheManager"))))
                    .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)),
                FieldDeclaration(
                    VariableDeclaration(ParseTypeName("MethodCacheConfiguration"))
                        .AddVariables(VariableDeclarator(Identifier("_methodCacheConfiguration"))))
                    .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)),
                FieldDeclaration(
                    VariableDeclaration(ParseTypeName("IServiceProvider"))
                        .AddVariables(VariableDeclarator(Identifier("_serviceProvider"))))
                    .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword))
            };

            var constructor = ConstructorDeclaration(Identifier(decoratorClassName))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(
                    Parameter(Identifier("decorated")).WithType(ParseTypeName(decoratedInterfaceName)),
                    Parameter(Identifier("cacheManager")).WithType(ParseTypeName("ICacheManager")),
                    Parameter(Identifier("methodCacheConfiguration")).WithType(ParseTypeName("MethodCacheConfiguration")),
                    Parameter(Identifier("serviceProvider")).WithType(ParseTypeName("IServiceProvider")))
                .WithBody(
                    Block(
                        ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                IdentifierName("_decorated"),
                                IdentifierName("decorated"))),
                        ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                IdentifierName("_cacheManager"),
                                IdentifierName("cacheManager"))),
                        ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                IdentifierName("_methodCacheConfiguration"),
                                IdentifierName("methodCacheConfiguration"))),
                        ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                IdentifierName("_serviceProvider"),
                                IdentifierName("serviceProvider")))));

            var methods = new List<MemberDeclarationSyntax>();

            // Generate methods with caching/invalidation logic
            foreach (var method in interfaceSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                var methodName = method.Name;
                var parameters = ParameterList(SeparatedList(method.Parameters.Select(p =>
                    Parameter(Identifier(p.Name)).WithType(ParseTypeName(p.Type.ToDisplayString())))));
                var parameterNames = ArgumentList(SeparatedList(method.Parameters.Select(p => Argument(IdentifierName(p.Name)))));
                var returnType = ParseTypeName(method.ReturnType.ToDisplayString());

                var attributes = method.GetAttributes();
                var cacheAttribute = attributes.FirstOrDefault(ad => IsCacheAttribute(ad.AttributeClass));
                var invalidateAttribute = attributes.FirstOrDefault(ad => IsCacheInvalidateAttribute(ad.AttributeClass));

                if (cacheAttribute != null)
                {
                    // Generate caching implementation
                    var methodImpl = GenerateCacheMethod(method, methodName, parameters, returnType);
                    methods.Add(methodImpl);
                }
                else if (invalidateAttribute != null)
                {
                    // Generate invalidation implementation
                    var methodImpl = GenerateInvalidateMethod(method, methodName, parameters, returnType, invalidateAttribute);
                    methods.Add(methodImpl);
                }
                else
                {
                    // Simple pass-through implementation
                    var methodImpl = MethodDeclaration(returnType, methodName)
                        .AddModifiers(Token(SyntaxKind.PublicKeyword))
                        .WithParameterList(parameters)
                        .WithBody(Block(
                            ReturnStatement(
                                InvocationExpression(
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("_decorated"),
                                        IdentifierName(methodName)))
                                    .WithArgumentList(parameterNames))));

                    methods.Add(methodImpl);
                }
            }

            return CompilationUnit()
                .AddUsings(
                    UsingDirective(ParseName("System")),
                    UsingDirective(ParseName("System.Threading.Tasks")),
                    UsingDirective(ParseName("MethodCache.Core")),
                    UsingDirective(ParseName("MethodCache.Core.Configuration")),
                    UsingDirective(ParseName("Microsoft.Extensions.DependencyInjection")),
                    UsingDirective(ParseName("System.Threading")),
                    UsingDirective(ParseName("System.Linq")))
                .AddMembers(
                    NamespaceDeclaration(ParseName(namespaceName))
                        .AddMembers(
                            ClassDeclaration(decoratorClassName)
                                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                                .AddBaseListTypes(SimpleBaseType(ParseTypeName(decoratedInterfaceName)))
                                .AddMembers(fields.ToArray())
                                .AddMembers(constructor)
                                .AddMembers(methods.ToArray())));
        }

        private static MethodDeclarationSyntax GenerateCacheMethod(IMethodSymbol method, string methodName, ParameterListSyntax parameters, TypeSyntax returnType)
        {
            // Void methods can't be cached, generate pass-through
            if (method.ReturnType.SpecialType == SpecialType.System_Void)
            {
                var parameterNames = ArgumentList(SeparatedList(method.Parameters.Select(p => Argument(IdentifierName(p.Name)))));
                return MethodDeclaration(returnType, methodName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                    .WithParameterList(parameters)
                    .WithBody(Block(
                        ExpressionStatement(
                            InvocationExpression(
                                MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    IdentifierName("_decorated"),
                                    IdentifierName(methodName)))
                                .WithArgumentList(parameterNames))));
            }
            
            var cacheAttribute = method.GetAttributes().FirstOrDefault(ad => IsCacheAttribute(ad.AttributeClass));
            var requireIdempotent = GetRequireIdempotentValue(cacheAttribute);
            
            var paramList = string.Join(", ", method.Parameters.Select(p => p.Name));
            var argsArrayItems = method.Parameters.Any() ? paramList : "";
            
            // Determine if this is an async method
            var isAsync = method.ReturnType.Name == "Task" && method.ReturnType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";
            
            // Extract the actual return type for caching
            string cacheReturnType;
            if (isAsync)
            {
                var namedReturnType = method.ReturnType as INamedTypeSymbol;
                if (namedReturnType?.IsGenericType == true && namedReturnType.TypeArguments.Length > 0)
                {
                    // Task<T> - extract T
                    cacheReturnType = namedReturnType.TypeArguments[0].ToDisplayString();
                }
                else
                {
                    // Task - use object
                    cacheReturnType = "object";
                }
            }
            else
            {
                // Synchronous method
                if (method.ReturnType.SpecialType == SpecialType.System_Void)
                {
                    cacheReturnType = "object"; // void methods can't be cached, but we use object as placeholder
                }
                else
                {
                    cacheReturnType = method.ReturnType.ToDisplayString();
                }
            }
            
            // Generate the appropriate factory lambda
            string factoryLambda;
            if (isAsync)
            {
                factoryLambda = $"async () => await _decorated.{methodName}({paramList})";
            }
            else
            {
                factoryLambda = $"() => Task.FromResult(_decorated.{methodName}({paramList}))";
            }
            
            // Generate the appropriate return statement
            string returnStatement;
            if (isAsync)
            {
                returnStatement = $"return _cacheManager.GetOrCreateAsync<{cacheReturnType}>(\"{methodName}\", args, {factoryLambda}, settings, keyGenerator, {requireIdempotent.ToString().ToLower()});";
            }
            else
            {
                returnStatement = $"return _cacheManager.GetOrCreateAsync<{cacheReturnType}>(\"{methodName}\", args, {factoryLambda}, settings, keyGenerator, {requireIdempotent.ToString().ToLower()}).GetAwaiter().GetResult();";
            }
            
            // Generate clean method body with proper formatting
            var methodBodyCode = $@"{{
            var args = new object[] {{ {argsArrayItems} }};
            var settings = _methodCacheConfiguration.GetMethodSettings(""{method.ContainingType.ToDisplayString()}.{methodName}"");
            var keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();
            {returnStatement}
        }}";

            var parsedBody = (BlockSyntax)SyntaxFactory.ParseStatement(methodBodyCode);

            return MethodDeclaration(returnType, methodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .WithParameterList(parameters)
                .WithBody(parsedBody);
        }

        private static MethodDeclarationSyntax GenerateInvalidateMethod(IMethodSymbol method, string methodName, ParameterListSyntax parameters, TypeSyntax returnType, AttributeData invalidateAttribute)
        {
            var tags = GetTagsFromInvalidateAttribute(invalidateAttribute);
            var tagsArray = tags.Any() ? $"new string[] {{ {string.Join(", ", tags.Select(t => $"\"{t}\""))} }}" : "new string[0]";
            var paramList = string.Join(", ", method.Parameters.Select(p => p.Name));

            // Create the invalidation body with proper formatting
            string methodBodyCode;
            if (method.ReturnType.SpecialType == SpecialType.System_Void)
            {
                methodBodyCode = $@"{{
            _cacheManager.InvalidateByTagsAsync({tagsArray}).GetAwaiter().GetResult();
            _decorated.{methodName}({paramList});
        }}";
            }
            else
            {
                methodBodyCode = $@"{{
            _cacheManager.InvalidateByTagsAsync({tagsArray}).GetAwaiter().GetResult();
            return _decorated.{methodName}({paramList});
        }}";
            }

            var parsedBody = (BlockSyntax)SyntaxFactory.ParseStatement(methodBodyCode);

            return MethodDeclaration(returnType, methodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .WithParameterList(parameters)
                .WithBody(parsedBody);
        }

        private static bool GetRequireIdempotentValue(AttributeData? cacheAttribute)
        {
            if (cacheAttribute?.NamedArguments.Any() == true)
            {
                var requireIdempotentArg = cacheAttribute.NamedArguments
                    .FirstOrDefault(arg => arg.Key == "RequireIdempotent");
                if (requireIdempotentArg.Value.Value is bool value)
                {
                    return value;
                }
            }
            return false;
        }

        private static List<string> GetTagsFromInvalidateAttribute(AttributeData invalidateAttribute)
        {
            var tags = new List<string>();
            
            if (invalidateAttribute?.NamedArguments.Any() == true)
            {
                var tagsArg = invalidateAttribute.NamedArguments
                    .FirstOrDefault(arg => arg.Key == "Tags");
                if (!tagsArg.Value.Values.IsDefaultOrEmpty)
                {
                    tags.AddRange(tagsArg.Value.Values.Select(v => v.Value?.ToString() ?? ""));
                }
            }
            
            return tags.Where(t => !string.IsNullOrEmpty(t)).ToList();
        }
    }
}
