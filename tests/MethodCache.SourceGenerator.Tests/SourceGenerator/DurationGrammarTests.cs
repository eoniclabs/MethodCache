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
    [InlineData("Duration = \"1m\", RefreshAhead = \"1m\"", "MC0023")]
    [InlineData("RefreshAhead = \"00:02:00\", Duration = \"1m\"", "MC0023")]
    [InlineData("SlidingExpiration = \"1m\", RefreshAhead = \"2m\"", "MC0023")]
    [InlineData("Duration = \"1h\", SlidingExpiration = \"1m\", RefreshAhead = \"1m\"", "MC0023")]
    public void InvalidSettings_AreGeneratorErrors(string setting, string id)
        => Assert.Contains(Generate("using MethodCache; public interface IExample { [Cache(" + setting + ")] int Read(); }").Diagnostics, d => d.Id == id);

    [Fact]
    public void RefreshAheadNotShorterThanDuration_NamesBothValues()
    {
        var diagnostic = Assert.Single(
            Generate("using MethodCache; public interface IExample { [Cache(Duration = \"1m\", RefreshAhead = \"2m\")] int Read(); }").Diagnostics,
            d => d.Id == "MC0023");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith("RefreshAhead = \"2m\" is not shorter than Duration = \"1m\"", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshAheadNotShorterThanSlidingExpiration_NamesBothValues()
    {
        var diagnostic = Assert.Single(
            Generate("using MethodCache; public interface IExample { [Cache(SlidingExpiration = \"1m\", RefreshAhead = \"2m\")] int Read(); }").Diagnostics,
            d => d.Id == "MC0023");

        Assert.StartsWith("RefreshAhead = \"2m\" is not shorter than SlidingExpiration = \"1m\"", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshAheadNotShorterThanDuration_OnCacheInterface_IsAGeneratorError()
    {
        const string code = """
            using MethodCache;
            [assembly: CacheInterface(typeof(System.IFormatProvider), "GetFormat", Duration = "1m", RefreshAhead = "90s")]
            """;

        Assert.Contains(Generate(code).Diagnostics, d => d.Id == "MC0023");
    }

    [Theory]
    [InlineData("Duration = \"1m\", RefreshAhead = \"59s\"")]
    [InlineData("Duration = \"1m\", RefreshAhead = \"off\"")]
    [InlineData("RefreshAhead = \"10m\"")]
    [InlineData("Duration = \"1m\", RefreshAhead = \"soon\"")]
    [InlineData("Duration = \"1h\", SlidingExpiration = \"5m\", RefreshAhead = \"1m\"")]
    [InlineData("SlidingExpiration = \"off\", RefreshAhead = \"10m\"")]
    public void RefreshAheadShorterThanDuration_OrWithoutBothValues_IsNotMC0023(string setting)
        => Assert.DoesNotContain(Generate("using MethodCache; public interface IExample { [Cache(" + setting + ")] int Read(); }").Diagnostics, d => d.Id == "MC0023");

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
