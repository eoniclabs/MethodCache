using System.Collections.Immutable;
using System.Reflection;
using MethodCache;
using MethodCache.CodeFixes;
using MethodCache.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace MethodCache.Analyzers.Tests.CodeFixes
{
    public class CodeFixTests
    {
        [Fact]
        public async Task MC0001_MovesCacheToTheInterfaceMethod()
        {
            var source = @"using System.Threading.Tasks;
using MethodCache;

public interface IUserService
{
    /// <summary>Gets a user.</summary>
    Task<string> GetAsync(int id);
}

public class UserService : IUserService
{
    [Cache(Duration = ""00:05:00"")]
    public Task<string> GetAsync(int id) => Task.FromResult("""");
}";

            var expected = @"using System.Threading.Tasks;
using MethodCache;

public interface IUserService
{
    /// <summary>Gets a user.</summary>
    [Cache(Duration = ""00:05:00"")]
    Task<string> GetAsync(int id);
}

public class UserService : IUserService
{
    public Task<string> GetAsync(int id) => Task.FromResult("""");
}";

            var (title, fixedSources) = await FixAsync(new MoveAttributeToInterfaceCodeFixProvider(), "MC0001", source);

            Assert.Equal("Move [Cache] to IUserService.GetAsync", title);
            Assert.Equal(expected, fixedSources[0]);
        }

        [Fact]
        public async Task MC0001_MovesAcrossFiles_AndAddsTheUsing()
        {
            var service = @"using System.Threading.Tasks;
using MethodCache;

public class UserService : IUserService
{
    [Cache]
    [System.Obsolete]
    public Task<string> GetAsync(int id) => Task.FromResult("""");
}";
            var contract = @"using System.Threading.Tasks;

public interface IUserService
{
    [System.ComponentModel.Description(""x"")]
    Task<string> GetAsync(int id);
}";

            var (_, fixedSources) = await FixAsync(new MoveAttributeToInterfaceCodeFixProvider(), "MC0001", service, contract);

            Assert.DoesNotContain("[Cache]", fixedSources[0]);
            Assert.Contains("    [System.Obsolete]\n    public Task<string> GetAsync", fixedSources[0].Replace("\r\n", "\n"));
            Assert.Equal(@"using System.Threading.Tasks;
using MethodCache;

public interface IUserService
{
    [System.ComponentModel.Description(""x"")]
    [Cache]
    Task<string> GetAsync(int id);
}", fixedSources[1].Replace("\r\n", "\n"));
        }

        [Fact]
        public async Task MC0001_WithoutAnInterface_RemovesTheAttribute()
        {
            var source = @"using MethodCache;

public class Users
{
    [CacheInvalidate(Tags = new[] { ""users"" })]
    public void Clear() { }
}";

            var (title, fixedSources) = await FixAsync(new MoveAttributeToInterfaceCodeFixProvider(), "MC0001", source);

            Assert.Equal("Remove [CacheInvalidate]", title);
            Assert.Equal(@"using MethodCache;

public class Users
{
    public void Clear() { }
}", fixedSources[0]);
        }

        [Theory]
        [InlineData("MCG001", "void Save(int id);")]
        [InlineData("MCG002", "Task SaveAsync(int id);")]
        [InlineData("MCG014", "IEnumerable<int> All();")]
        public async Task UncacheableMethod_RemoveCache(string id, string member)
        {
            var source = $@"using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;

public interface IStore
{{
    [Cache]
    {member}
}}";

            var (title, fixedSources) = await FixAsync(new RemoveCacheAttributeCodeFixProvider(), id, source);

            Assert.Equal("Remove [Cache]", title);
            Assert.Equal($@"using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;

public interface IStore
{{
    {member}
}}", fixedSources[0]);
        }

        [Fact]
        public async Task MCG009_ReplacesTheUnknownParameterWithTheClosestName()
        {
            var source = @"using System.Threading.Tasks;
using MethodCache;

public interface IUserService
{
    [Cache(Tags = new[] { ""users"", ""user:{usrId}"", ""tenant:{tenant}:user:{usrId}"" })]
    Task<string> GetAsync(int userId, string tenant);
}";

            var (title, fixedSources) = await FixAsync(new FixTagParameterCodeFixProvider(), "MCG009", source);

            Assert.Equal("Use parameter 'userId' in the tag", title);
            Assert.Contains(@"new[] { ""users"", ""user:{userId}"", ""tenant:{tenant}:user:{userId}"" }", fixedSources[0]);
        }

        [Fact]
        public void EveryDiagnostic_LinksToItsSectionInTheGuide()
        {
            var guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "user-guide", "DIAGNOSTICS.md"));
            var descriptors = new MethodCacheAnalyzer().SupportedDiagnostics
                .Concat(GeneratorDescriptors())
                .ToList();

            Assert.True(descriptors.Count >= 15);
            foreach (var descriptor in descriptors)
            {
                Assert.Equal("https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + descriptor.Id.ToLowerInvariant(), descriptor.HelpLinkUri);
                Assert.Contains("\n## " + descriptor.Id + "\n", guide.Replace("\r\n", "\n"));
            }
        }

        private static IEnumerable<DiagnosticDescriptor> GeneratorDescriptors()
            => typeof(MethodCacheGenerator).GetNestedType("Diagnostics", BindingFlags.NonPublic)!
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
                .Select(f => (DiagnosticDescriptor)f.GetValue(null)!);

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MethodCache.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("MethodCache.sln not found");
        }

        /// <summary>Finds the diagnostic, applies the provider's first fix and returns every document's new text.</summary>
        private static async Task<(string Title, string[] Sources)> FixAsync(CodeFixProvider provider, string diagnosticId, params string[] sources)
        {
            using var workspace = new AdhocWorkspace();
            var project = workspace.CurrentSolution
                .AddProject("Test", "Test", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithMetadataReferences(References());
            var documentIds = new List<DocumentId>();
            for (var i = 0; i < sources.Length; i++)
            {
                var document = project.AddDocument($"File{i}.cs", SourceText.From(sources[i]));
                documentIds.Add(document.Id);
                project = document.Project;
            }

            var compilation = (await project.GetCompilationAsync())!;
            var diagnostics = await DiagnosticsAsync(compilation);
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == diagnosticId) ?? throw new InvalidOperationException(string.Join("\n", diagnostics));
            var target = project.GetDocument(diagnostic.Location.SourceTree!)!;

            var actions = new List<CodeAction>();
            var context = new CodeFixContext(target, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
            await provider.RegisterCodeFixesAsync(context);
            var first = Assert.IsAssignableFrom<CodeAction>(actions.FirstOrDefault());

            var operations = await first.GetOperationsAsync(CancellationToken.None);
            var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            var texts = new List<string>();
            foreach (var id in documentIds)
            {
                texts.Add((await changed.GetDocument(id)!.GetTextAsync()).ToString());
            }

            return (first.Title, texts.ToArray());
        }

        private static async Task<ImmutableArray<Diagnostic>> DiagnosticsAsync(Compilation compilation)
        {
            var analyzerDiagnostics = await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MethodCacheAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();

            CSharpGeneratorDriver.Create(new MethodCacheGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out _, out var generatorDiagnostics);

            return analyzerDiagnostics.AddRange(generatorDiagnostics);
        }

        private static IEnumerable<MetadataReference> References()
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            return trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
        }
    }
}
