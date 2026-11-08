using System.Diagnostics;
using Xunit;

namespace MethodCache.Core.Tests.Packaging;

/// <summary>
/// MethodCacheAnalysisLevel in the package's build targets turns off the analyzer rules that came after the given
/// version. The targets run in MSBuild on a project that lists made-up rules of later versions, or only the rules the
/// targets list themselves.
/// </summary>
public sealed class AnalysisLevelTargetsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("methodcache-analysis-level-").FullName;

    [Fact]
    public async Task Level_TurnsOffTheRulesThatCameLater()
    {
        var config = await RunAsync("1.1");

        Assert.Contains("is_global = true", config);
        Assert.Contains("dotnet_diagnostic.MC9002.severity = none", config);
        Assert.Contains("dotnet_diagnostic.MC9003.severity = none", config);
        Assert.DoesNotContain("MC9001", config);
    }

    [Fact]
    public async Task Level_OfTheLatestVersion_TurnsOffNothing()
    {
        var config = await RunAsync("2.0");

        Assert.DoesNotContain("dotnet_diagnostic", config);
    }

    [Fact]
    public async Task NoLevel_WritesNoFile()
    {
        Assert.Null(await RunAsync(level: null));
    }

    [Fact]
    public async Task Level_WithTheRulesAsShipped_Builds()
    {
        // The package lists no rule after 1.0 yet; an empty list must not fail the build
        var config = await RunAsync("1.0", rules: "");

        Assert.Contains("is_global = true", config);
        Assert.DoesNotContain("dotnet_diagnostic", config);
    }

    [Fact]
    public async Task Level_ThatIsNoVersion_FailsTheBuild()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("latest"));

        Assert.Contains("MCB001", error.Message);
    }

    private const string MadeUpRules = """
        <MethodCacheRule Include="MC9001" Since="1.1" />
        <MethodCacheRule Include="MC9002" Since="1.1.1" />
        <MethodCacheRule Include="MC9003" Since="1.2" />
        """;

    /// <summary>
    /// Runs the targets and returns the global config they wrote, or null when they wrote none. <paramref name="rules"/>
    /// are added to the rules the targets list themselves.
    /// </summary>
    private async Task<string?> RunAsync(string? level, string rules = MadeUpRules)
    {
        var targets = Path.Combine(RepositoryRoot(), "src", "MethodCache.Core", "build", "MethodCache.targets");
        var project = Path.Combine(_directory, "app.proj");
        await File.WriteAllTextAsync(project, $"""
            <Project>
              <PropertyGroup>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
                {(level == null ? "" : $"<MethodCacheAnalysisLevel>{level}</MethodCacheAnalysisLevel>")}
              </PropertyGroup>
              <ItemGroup>
                {rules}
              </ItemGroup>
              <Import Project="{targets}" />
              <Target Name="CoreCompile" />
            </Project>
            """);

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "msbuild", project, "-t:CoreCompile", "-nologo", "-v:q" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(output);

        var config = Path.Combine(_directory, "obj", "MethodCacheAnalysisLevel.globalconfig");
        return File.Exists(config) ? await File.ReadAllTextAsync(config) : null;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MethodCache.sln"))) return directory.FullName;
        }
        throw new InvalidOperationException("The repository root, with MethodCache.sln, is not above " + AppContext.BaseDirectory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
