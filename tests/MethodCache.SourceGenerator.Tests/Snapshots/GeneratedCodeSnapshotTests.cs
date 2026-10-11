using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VerifyXunit;
using Xunit;

namespace MethodCache.SourceGenerator.Tests.Snapshots;

/// <summary>
/// Snapshots of the code the generator writes for representative interfaces. A change to the generated code shows
/// up as a diff of the <c>.verified.cs</c> files in review, so it is a deliberate change rather than a side effect.
/// When a change is intended, accept the new <c>.received.*</c> files (or delete the old snapshots and run again).
/// </summary>
public class GeneratedCodeSnapshotTests
{
    [Fact]
    public Task AsyncMethodWithTagsAndInvalidation() => VerifyGenerated("""
        using System.Threading;
        using System.Threading.Tasks;
        using MethodCache;

        namespace App;

        public record User(int Id, string Name);

        public interface IUserService
        {
            [Cache(Duration = "00:10:00", Tags = new[] { "users", "user:{userId}" })]
            Task<User> GetUserAsync(int userId, CancellationToken cancellationToken = default);

            [CacheInvalidate(Tags = new[] { "users", "user:{userId}" })]
            Task UpdateUserAsync(int userId, string name);

            Task<int> CountAsync();
        }

        public class UserService : IUserService
        {
            public Task<User> GetUserAsync(int userId, CancellationToken cancellationToken = default) => Task.FromResult(new User(userId, "n"));
            public Task UpdateUserAsync(int userId, string name) => Task.CompletedTask;
            public Task<int> CountAsync() => Task.FromResult(0);
        }
        """);

    [Fact]
    public Task SyncAndValueTaskMethods() => VerifyGenerated("""
        using System.Threading.Tasks;
        using MethodCache;

        namespace App;

        public interface IPricing
        {
            [Cache] decimal Price(string sku, int quantity = 1);
            [Cache] ValueTask<string?> FindAsync(string? sku);
        }

        public class Pricing : IPricing
        {
            public decimal Price(string sku, int quantity = 1) => 1m;
            public ValueTask<string?> FindAsync(string? sku) => default;
        }
        """);

    [Fact]
    public Task OverloadsAndGenericMethods() => VerifyGenerated("""
        using System.Threading.Tasks;
        using MethodCache;

        namespace App;

        public interface ICatalog
        {
            [Cache] Task<string> Get(int id);
            [Cache] Task<string> Get(string slug);
            [Cache] Task<T?> Load<T>(int id) where T : class;
        }

        public class Catalog : ICatalog
        {
            public Task<string> Get(int id) => Task.FromResult("");
            public Task<string> Get(string slug) => Task.FromResult("");
            public Task<T?> Load<T>(int id) where T : class => Task.FromResult<T?>(null);
        }
        """);

    [Fact]
    public Task GenericInterface() => VerifyGenerated("""
        using System.Threading.Tasks;
        using MethodCache;

        namespace App;

        public record User(int Id);

        public interface IRepository<T>
        {
            [Cache(Tags = new[] { "repo" })] Task<T?> GetAsync(int id);
        }

        public class UserRepository : IRepository<User>
        {
            public Task<User?> GetAsync(int id) => Task.FromResult<User?>(new User(id));
        }
        """);

    private static Task VerifyGenerated(string source)
    {
        var compilation = CSharpCompilation.Create(
            "SnapshotApp",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12)) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new[] { new MethodCacheGenerator().AsSourceGenerator() }, parseOptions: new CSharpParseOptions(LanguageVersion.CSharp12))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        return Verifier.Verify(driver);
    }

    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceCollection).Assembly.Location))
            .ToArray();
}
