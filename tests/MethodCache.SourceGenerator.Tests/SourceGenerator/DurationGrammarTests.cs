using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator;

public class DurationGrammarTests
{
    [Theory]
    [MemberData(nameof(MethodCache.TestSupport.DurationCorpus.Invalid), MemberType = typeof(MethodCache.TestSupport.DurationCorpus))]
    public void InvalidExplicitDuration_IsAGeneratorError_WithoutAnalyzers(string value)
    {
        var code = "using MethodCache; public interface IExample { [Cache(Duration = \"" + value + "\")] int Read(); }";
        var result = Generate(code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.GetMessage().Contains("Duration", StringComparison.Ordinal));
    }
    [Theory]
    [MemberData(nameof(MethodCache.TestSupport.DurationCorpus.Values), MemberType = typeof(MethodCache.TestSupport.DurationCorpus))]
    public void ValidExplicitDuration_EmitsExactTicks(string text, long ticks)
    {
        var result = Generate("using MethodCache; public interface IExample { [Cache(Duration = \"" + text + "\")] int Read(); }");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Results.SelectMany(r => r.GeneratedSources), source => source.SourceText.ToString().Contains("FromTicks(" + ticks + "L)", StringComparison.Ordinal));
    }

    [Fact]
    public void DurationDiagnostic_PreservesExistingMC0004Meaning()
        => Assert.Contains(Generate("using MethodCache; public interface IReader { [Cache(Duration = \"0s\")] int Read(); }").Diagnostics,
            diagnostic => diagnostic.Id == "MC0004");

    [Theory]
    [InlineData("Version = -1", "MC0021")]
    [InlineData("FailSafeTimeout = \"0s\"", "MC0004")]
    [InlineData("FailSafeRetryAfter = \"0s\"", "MC0004")]
    [InlineData("Duration = null", "MC0004")]
    public void InvalidSettings_AreGeneratorErrors(string setting, string id)
        => Assert.Contains(Generate("using MethodCache; public interface IExample { [Cache(" + setting + ")] int Read(); }").Diagnostics, d => d.Id == id);

    [Theory]
    [InlineData("[Cache(\"old-group\")] int Read();")]
    [InlineData("[Cache] int Read([CacheKey] int id);")]
    [InlineData("[Cache] int Read([CacheKey(UseAsRawKey = true)] string id);")]
    public void RetiredAttributeForms_AreCompilerErrors(string member)
    {
        var code = "using MethodCache; public interface IExample { " + member + " }";
        Assert.Contains(Compilation(code).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    private static CSharpCompilation Compilation(string code)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
        return CSharpCompilation.Create("DurationGrammar", [CSharpSyntaxTree.ParseText(code)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorDriverRunResult Generate(string code)
    {
        var compilation = Compilation(code);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MethodCacheGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

}
