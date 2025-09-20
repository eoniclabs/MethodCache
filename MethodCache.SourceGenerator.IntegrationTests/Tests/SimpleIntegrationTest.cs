using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Simple integration test to verify source generator compilation works
/// </summary>
public class SimpleIntegrationTest
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public SimpleIntegrationTest(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_BasicCompilation_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public interface ISimpleService
    {
        [Cache(Duration = ""00:01:00"")]
        Task<string> GetDataAsync(string key);
    }

    public class SimpleService : ISimpleService
    {
        public virtual async Task<string> GetDataAsync(string key)
        {
            await Task.Delay(10);
            return $""Data-{key}"";
        }
    }
}";

        // Act: Compile with source generator
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        
        // Debug: Output generated sources
        _output.WriteLine("=== GENERATED SOURCES ===");
        foreach (var (fileName, source) in testAssembly.GeneratedSources)
        {
            _output.WriteLine($"\n--- {fileName} ---");
            _output.WriteLine(source);
        }

        // Verify compilation succeeded
        Assert.NotNull(testAssembly.Assembly);
        Assert.True(testAssembly.GeneratedSources.Count > 0);

        // Verify types were generated
        var serviceType = testAssembly.Assembly.GetType("TestNamespace.ISimpleService");
        Assert.NotNull(serviceType);

        _output.WriteLine($"✅ Basic compilation test passed! Generated {testAssembly.GeneratedSources.Count} source files");
    }
}