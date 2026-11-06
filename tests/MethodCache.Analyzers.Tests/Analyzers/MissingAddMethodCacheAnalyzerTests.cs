using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class MissingAddMethodCacheAnalyzerTests
    {
        private const string OwnServices = @"using System.Threading.Tasks;
using MethodCache;

public interface IPrices { [Cache] Task<decimal> GetAsync(int id); }
";

        private const string Program = @"
public static class Program
{
    public static void Main()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Register(services);
    }

    static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
}
";

        [Fact]
        public async Task ProgramWithOwnCachedServices_AndNoAddMethodCache_IsReported()
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(OwnServices + Program, OutputKind.ConsoleApplication));

            Assert.Equal("MC0030", diagnostic.Id);
            Assert.Contains("'Host' has cached services in its own code", diagnostic.GetMessage());
            Assert.True(diagnostic.Location.IsInSource);
        }

        [Fact]
        public async Task ProgramReferencingAProjectWithCachedServices_IsReported()
        {
            var data = Library("Data", MarkerSource("MethodCache.CachedServices"));

            var diagnostic = Assert.Single(await AnalyzeAsync(Program, OutputKind.ConsoleApplication, data));

            Assert.Contains("has cached services in Data", diagnostic.GetMessage());
        }

        [Fact]
        public async Task ProgramThatCallsAddMethodCache_IsNotReported()
        {
            var source = OwnServices + Program.Replace("Register(services);", "Microsoft.Extensions.DependencyInjection.MethodCacheServiceCollectionExtensions.AddMethodCache(services);");

            Assert.Empty(await AnalyzeAsync(source, OutputKind.ConsoleApplication));
        }

        // The shared setup method counts only when the program calls it: a program that references the project but never
        // calls its setup method sets up nothing
        [Fact]
        public async Task ProgramReferencingAProjectThatCallsAddMethodCache_ButNotCallingItsMethod_IsReported()
        {
            var common = Library("Common", MarkerSource("MethodCache.CachedServices") + SetupSource);

            var diagnostic = Assert.Single(await AnalyzeAsync(Program, OutputKind.ConsoleApplication, common));

            Assert.Contains("calls neither AddMethodCache nor a method of a referenced project that calls it", diagnostic.GetMessage());
        }

        [Fact]
        public async Task ProgramCallingAReferencedProjectsMethodThatCallsAddMethodCache_IsNotReported()
        {
            var common = Library("Common", MarkerSource("MethodCache.CachedServices") + SetupSource);

            var source = Program.Replace("Register(services);", "Common.Setup.AddCommonCache(services);");

            Assert.Empty(await AnalyzeAsync(source, OutputKind.ConsoleApplication, common));
        }

        [Fact]
        public async Task ProgramCallingItAsAnExtensionMethodOrMethodGroup_IsNotReported()
        {
            var common = Library("Common", MarkerSource("MethodCache.CachedServices") + SetupSource);

            var extension = "using Common;\n" + Program.Replace("Register(services);", "services.AddCommonCache();");
            var methodGroup = Program.Replace("Register(services);",
                "System.Action<Microsoft.Extensions.DependencyInjection.IServiceCollection> setup = Common.Setup.AddCommonCache; setup(services);");

            Assert.Empty(await AnalyzeAsync(extension, OutputKind.ConsoleApplication, common));
            Assert.Empty(await AnalyzeAsync(methodGroup, OutputKind.ConsoleApplication, common));
        }

        private const string SetupSource = @"
[assembly: System.Reflection.AssemblyMetadata(""MethodCache.AddsMethodCache"", ""M:Common.Setup.AddCommonCache(Microsoft.Extensions.DependencyInjection.IServiceCollection)"")]
namespace Common
{
    public static class Setup
    {
        public static void AddCommonCache(this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
            => Microsoft.Extensions.DependencyInjection.MethodCacheServiceCollectionExtensions.AddMethodCache(services);
    }
}
";

        [Fact]
        public async Task LibraryTestProjectAndProgramWithoutCachedServices_AreNotReported()
        {
            Assert.Empty(await AnalyzeAsync(OwnServices + "public static class Nothing { }", OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(await AnalyzeAsync(OwnServices + Program, OutputKind.ConsoleApplication, Library("xunit.core", "")));
            Assert.Empty(await AnalyzeAsync(Program, OutputKind.ConsoleApplication, Library("Data", "")));
        }

        private static string MarkerSource(string key) => $"[assembly: System.Reflection.AssemblyMetadata(\"{key}\", \"true\")]\n";

        private static List<MetadataReference> References()
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            return trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(typeof(ServiceCollection).Assembly.Location))
                .ToList();
        }

        private static MetadataReference Library(string name, string source)
        {
            var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            Assert.True(compilation.Emit(stream).Success);
            return MetadataReference.CreateFromImage(stream.ToArray());
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, OutputKind kind, params MetadataReference[] libraries)
        {
            var compilation = CSharpCompilation.Create("Host", [CSharpSyntaxTree.ParseText(source)], References().Concat(libraries),
                new CSharpCompilationOptions(kind));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MissingAddMethodCacheAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
