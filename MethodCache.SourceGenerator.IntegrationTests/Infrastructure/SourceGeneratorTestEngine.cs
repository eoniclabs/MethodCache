using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.SourceGenerator;
using MethodCache.Core;
using MethodCache.Abstractions.Sources;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.KeyGeneration;

namespace MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

/// <summary>
/// Engine for compiling and testing source-generated code in real-world scenarios
/// </summary>
public class SourceGeneratorTestEngine
{
    private readonly List<MetadataReference> _references;

    public SourceGeneratorTestEngine()
    {
        _references = GetRequiredReferences();
    }

    /// <summary>
    /// Compiles source code with the MethodCache source generator and returns a test assembly
    /// </summary>
    public Task<GeneratedTestAssembly> CompileWithSourceGeneratorAsync(
        string sourceCode, 
        string assemblyName = "TestAssembly")
    {
        // Parse the source code
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        // Create compilation
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            _references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug));

        // Apply source generator
        var generator = new MethodCacheGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, 
            out var outputCompilation, 
            out var diagnostics);

        // Check for compilation errors
        var allDiagnostics = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (allDiagnostics.Any())
        {
            var errors = string.Join("\n", allDiagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Compilation failed:\n{errors}");
        }

        // Emit to memory stream
        using var stream = new MemoryStream();
        var emitResult = outputCompilation.Emit(stream);

        if (!emitResult.Success)
        {
            var errors = string.Join("\n", emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException($"Emit failed:\n{errors}");
        }

        // Load the assembly
        stream.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(stream.ToArray());

        // Get generated sources for debugging
        var generatedSources = new Dictionary<string, string>();
        var runResult = driver.GetRunResult();
        
        foreach (var result in runResult.Results)
        {
            foreach (var generated in result.GeneratedSources)
            {
                generatedSources[generated.HintName] = generated.SourceText.ToString();
            }
        }

        return Task.FromResult(new GeneratedTestAssembly(assembly, generatedSources, diagnostics.ToList()));
    }

    /// <summary>
    /// Creates a service provider with MethodCache configured for testing
    /// </summary>
    public IServiceProvider CreateTestServiceProvider(GeneratedTestAssembly testAssembly, Action<IServiceCollection>? configureServices = null, Action<string>? logger = null)
    {
        var services = new ServiceCollection();

        // Add all required MethodCache infrastructure for testing
        services.AddSingleton<ICacheManager, TestMockCacheManager>();
        // Add additional dependencies that might be needed
        services.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();

        // Add default metrics provider (will be overridden by tests)
        services.AddSingleton<ICacheMetricsProvider, TestCacheMetricsProvider>();

        // Add policy registry and resolver services (required by generated decorators)
        // Add a dummy policy source to satisfy PolicyResolver's requirement for at least one source
        services.AddSingleton<PolicySourceRegistration>(_ => new PolicySourceRegistration(new EmptyPolicySource(), 0));
        PolicyRegistrationExtensions.EnsurePolicyServices(services);

        // Register generated services using reflection
        RegisterGeneratedServices(services, testAssembly.Assembly, logger);

        // Allow additional service configuration
        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private void RegisterGeneratedServices(IServiceCollection services, Assembly assembly, Action<string>? logger = null)
    {
        // Look for generated extension methods like AddITestServiceWithCaching
        var extensionTypes = assembly.GetTypes()
            .Where(t => t.IsStatic() && t.Name.Contains("Extensions"))
            .ToList();

        logger?.Invoke($"Found {extensionTypes.Count} extension types: {string.Join(", ", extensionTypes.Select(t => t.Name))}");

        foreach (var extensionType in extensionTypes)
        {
            var methods = extensionType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Add") && m.Name.EndsWith("WithCaching"))
                .ToList();

            logger?.Invoke($"Found {methods.Count} registration methods in {extensionType.Name}: {string.Join(", ", methods.Select(m => m.Name))}");
            
            foreach (var method in methods)
            {
                logger?.Invoke($"Method: {method.Name}, Parameters: {string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
            }

            foreach (var method in methods)
            {
                try
                {
                    // Call the generated AddXWithCaching method
                    // These methods typically take (IServiceCollection, Func<IServiceProvider, object>)
                    var parameters = method.GetParameters();
                    if (parameters.Length == 2 && 
                        parameters[0].ParameterType == typeof(IServiceCollection))
                    {
                        // Create a factory that returns a mock implementation
                        var serviceType = GetServiceTypeFromMethod(method);
                        if (serviceType != null)
                        {
                            var mockImplementation = CreateMockImplementation(assembly, serviceType);
                            logger?.Invoke($"Created mock implementation for {serviceType.Name}");
                            
                            // Create factory delegate using reflection to avoid type coercion issues
                            var factoryType = parameters[1].ParameterType; // Should be Func<IServiceProvider, ServiceType>
                            var createFactoryMethod = typeof(SourceGeneratorTestEngine)
                                .GetMethod(nameof(CreateTypedFactory), BindingFlags.NonPublic | BindingFlags.Instance)!
                                .MakeGenericMethod(serviceType);
                            
                            var factoryDelegate = createFactoryMethod.Invoke(this, new object[] { mockImplementation });

                            if (factoryDelegate != null)
                            {
                                method.Invoke(null, new object[] { services, factoryDelegate });
                            }
                            logger?.Invoke($"Successfully registered {method.Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't fail - some generated methods might have different signatures
                    logger?.Invoke($"Warning: Could not register {method.Name}: {ex.Message}");
                    logger?.Invoke($"Stack trace: {ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        logger?.Invoke($"Inner exception: {ex.InnerException.Message}");
                        logger?.Invoke($"Inner stack trace: {ex.InnerException.StackTrace}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates a typed factory delegate for service registration
    /// </summary>
    private Func<IServiceProvider, T> CreateTypedFactory<T>(T implementation) where T : class
    {
        return _ => implementation;
    }

    /// <summary>
    /// Helper method to create standardized test source code
    /// </summary>
    public static string CreateTestSourceCode(string interfaceAndImplementation, string namespaceName = "TestNamespace")
    {
        return $@"{UniversalTestModels.GetRequiredUsings()}

namespace {namespaceName}
{{
{UniversalTestModels.GetCompleteModelDefinitions()}

{interfaceAndImplementation}
}}";
    }

    private Type? GetServiceTypeFromMethod(MethodInfo method)
    {
        // Extract interface type from method name like AddIUserServiceWithCaching
        var serviceName = method.Name.Substring(3); // Remove "Add"
        serviceName = serviceName.Substring(0, serviceName.Length - 12); // Remove "WithCaching"
        
        // The service type should be in the test assembly, not the extension assembly
        // Look at the method's parameter types to find the correct type
        var parameters = method.GetParameters();
        if (parameters.Length >= 2)
        {
            var factoryParam = parameters[1];
            if (factoryParam.ParameterType.IsGenericType && 
                factoryParam.ParameterType.GetGenericTypeDefinition() == typeof(Func<,>))
            {
                // Get the return type of the Func<IServiceProvider, ServiceType>
                var genericArgs = factoryParam.ParameterType.GetGenericArguments();
                if (genericArgs.Length == 2)
                {
                    return genericArgs[1]; // ServiceType
                }
            }
        }
        
        return null;
    }

    private object CreateMockImplementation(Assembly assembly, Type serviceType)
    {
        // Find a concrete implementation in the assembly
        var implementation = assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && serviceType.IsAssignableFrom(t));

        if (implementation != null)
        {
            return Activator.CreateInstance(implementation) ?? 
                   throw new InvalidOperationException($"Could not create instance of {implementation.Name}");
        }

        // Create a dynamic proxy if no implementation found
        return CreateDynamicProxy(serviceType);
    }

    private object CreateDynamicProxy(Type serviceType)
    {
        // For now, throw - in a real implementation you might use Castle DynamicProxy
        throw new NotImplementedException($"Dynamic proxy creation for {serviceType.Name} not implemented. Please provide a concrete implementation in your test code.");
    }

    private static List<MetadataReference> GetRequiredReferences()
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MethodCache.Abstractions.Policies.CachePolicy).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MethodCache.Core.Runtime.KeyGeneration.ICacheKeyGenerator).Assembly.Location), // Include Core assembly for Runtime types
            MetadataReference.CreateFromFile(typeof(SourceGeneratorTestEngine).Assembly.Location), // Include this assembly for test models
        };

        // Try to load additional required assemblies, ignoring failures
        var assemblyNames = new[]
        {
            "System.Runtime",
            "System.Threading.Tasks",
            "System.Collections",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "System.ComponentModel",
            "System.Linq.Expressions",
            "System.Private.CoreLib"
        };

        foreach (var assemblyName in assemblyNames)
        {
            try
            {
                var assembly = Assembly.Load(assemblyName);
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
            catch
            {
                // Ignore missing assemblies
            }
        }

        return references;
    }
}

/// <summary>
/// Represents a compiled assembly with generated code and metadata
/// </summary>
public class GeneratedTestAssembly
{
    public Assembly Assembly { get; }
    public IReadOnlyDictionary<string, string> GeneratedSources { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public GeneratedTestAssembly(
        Assembly assembly, 
        IReadOnlyDictionary<string, string> generatedSources,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Assembly = assembly;
        GeneratedSources = generatedSources;
        Diagnostics = diagnostics;
    }

    public void DumpGeneratedSources(TextWriter writer)
    {
        writer.WriteLine("=== GENERATED SOURCES ===");
        foreach (var (fileName, source) in GeneratedSources)
        {
            writer.WriteLine($"\n--- {fileName} ---");
            writer.WriteLine(source);
        }
        
        if (Diagnostics.Any())
        {
            writer.WriteLine("\n=== DIAGNOSTICS ===");
            foreach (var diagnostic in Diagnostics)
            {
                writer.WriteLine($"{diagnostic.Severity}: {diagnostic.GetMessage()}");
            }
        }
    }
}

/// <summary>
/// Extension methods for testing
/// </summary>
public static class TypeExtensions
{
    public static bool IsStatic(this Type type)
    {
        return type.IsAbstract && type.IsSealed;
    }
}

/// <summary>
/// Empty policy source for testing scenarios where no policies are needed
/// </summary>
internal sealed class EmptyPolicySource : IPolicySource
{
    public string SourceId => "empty";

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(Array.Empty<PolicySnapshot>());
    }

    public async IAsyncEnumerable<PolicyChange> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}



