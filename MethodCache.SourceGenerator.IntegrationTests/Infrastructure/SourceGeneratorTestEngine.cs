using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.SourceGenerator;
using MethodCache.Core;

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
    public IServiceProvider CreateTestServiceProvider(GeneratedTestAssembly testAssembly, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();

        // Add basic MethodCache services without scanning assemblies
        services.AddSingleton<ICacheMetricsProvider, TestCacheMetricsProvider>();

        // Register generated services using reflection
        RegisterGeneratedServices(services, testAssembly.Assembly);

        // Allow additional service configuration
        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private void RegisterGeneratedServices(IServiceCollection services, Assembly assembly)
    {
        // Look for generated extension methods like AddITestServiceWithCaching
        var extensionTypes = assembly.GetTypes()
            .Where(t => t.IsStatic() && t.Name.Contains("Extensions"))
            .ToList();

        foreach (var extensionType in extensionTypes)
        {
            var methods = extensionType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Add") && m.Name.EndsWith("WithCaching"))
                .ToList();

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
                            Func<IServiceProvider, object> factory = _ => mockImplementation;
                            
                            method.Invoke(null, new object[] { services, factory });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't fail - some generated methods might have different signatures
                    Console.WriteLine($"Warning: Could not register {method.Name}: {ex.Message}");
                }
            }
        }
    }

    private Type? GetServiceTypeFromMethod(MethodInfo method)
    {
        // Extract interface type from method name like AddITestServiceWithCaching
        var serviceName = method.Name.Substring(3); // Remove "Add"
        serviceName = serviceName.Substring(0, serviceName.Length - 12); // Remove "WithCaching"
        
        return method.DeclaringType?.Assembly.GetType(serviceName) ?? 
               method.DeclaringType?.Assembly.GetTypes().FirstOrDefault(t => t.Name == serviceName);
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
            MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MethodCache.Core.CacheAttribute).Assembly.Location),
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