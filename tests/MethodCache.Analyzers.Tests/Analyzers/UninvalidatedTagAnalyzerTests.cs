using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class UninvalidatedTagAnalyzerTests
    {
        // Stand-ins for MethodCache.EntityFrameworkCore and EF Core, which the test doesn't reference
        private const string EntityFrameworkCore = @"
namespace Microsoft.EntityFrameworkCore
{
    public class DbContext { }
    public static class MethodCacheDbContextExtensions { public static void InvalidateAfterCommit(this DbContext context, params string[] tags) { } }
}
namespace MethodCache.EntityFrameworkCore
{
    public sealed class EntityFrameworkCoreOptions
    {
        public EntityFrameworkCoreOptions Entity<TEntity>(params string[] tagTemplates) where TEntity : class => this;
        public EntityFrameworkCoreOptions Entity<TEntity>(System.Action<EntityTagsBuilder<TEntity>> configure) where TEntity : class => this;
    }
    public sealed class EntityTagsBuilder<TEntity> where TEntity : class
    {
        public EntityTagsBuilder<TEntity> Tags(params string[] tagTemplates) => this;
        public EntityTagsBuilder<TEntity> WhenDeleted(params string[] tagTemplates) => this;
    }
}
";

        private const string Usings = @"
using System.Threading.Tasks;
using MethodCache;
using MethodCache.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed record Product(int Id);
";

        private const string Reads = @"
public interface IProductReads
{
    [Cache(Duration = ""10m"", Tags = [""product:{id}""])]
    Task<Product> GetAsync(int id);
}
";

        /// <summary>A write that invalidates <paramref name="body"/>, with the services a write may use.</summary>
        private static string Write(string body) => Usings + Reads + @"
public static class CacheChanges
{
    [InvalidatesCache]
    public static void Changed(this DbContext db, string tag) { }

    public static void Logged(this DbContext db, string tag) { }
}

public sealed class Writes
{
    public async Task SaveAsync(int id, string tag, DbContext db, ICacheManager cache, IMethodCacheKeys keys, EntityFrameworkCoreOptions ef)
    {
        " + body + @"
        await Task.CompletedTask;
    }
}";

        [Theory]
        [InlineData("await cache.InvalidateTagAsync(\"product:42\");")]
        [InlineData("await cache.InvalidateTagAsync($\"product:{id}\");")]
        [InlineData("await cache.InvalidateTagsAsync(new[] { \"orders\", \"product:1\" });")]
        [InlineData("await cache.InvalidateTagsAsync([\"product:1\"]);")]
        [InlineData("await cache.InvalidateTagAsync(keys.Tag<IProductReads>(r => r.GetAsync(id), \"product:{id}\"));")]
        [InlineData("db.InvalidateAfterCommit(\"orders\", \"product:7\");")]
        [InlineData("ef.Entity<Product>(\"product:{Id}\");")]
        [InlineData("ef.Entity<Product>(p => p.Tags(\"products\").WhenDeleted(\"product:{Id}\"));")]
        [InlineData("db.Changed(\"product:1\");")]
        [InlineData("await cache.InvalidateTagAsync($\"{tag}\");")]
        [InlineData("await cache.InvalidateTagAsync($\"product:{id}:extra\"); await cache.InvalidateTagAsync($\"{tag}:{id}\");")]
        public async Task A_tag_that_the_project_invalidates_is_not_reported(string invalidation)
        {
            Assert.Empty(await AnalyzeAsync(Write(invalidation)));
        }

        [Theory]
        [InlineData("await cache.InvalidateTagAsync(\"orders\");")]
        [InlineData("await cache.InvalidateTagAsync(\"products:1\");")]
        [InlineData("await cache.InvalidateTagAsync(\"product\");")]
        [InlineData("await cache.InvalidateTagAsync(tag);")]
        [InlineData("await cache.InvalidateTagAsync(\"orders\"); db.Logged(\"product:1\");")]
        [InlineData("ef.Entity<Product>(\"products\");")]
        public async Task A_tag_that_nothing_in_the_project_invalidates_is_reported(string invalidation)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Write(invalidation)));

            Assert.Equal("MC0028", diagnostic.Id);
            Assert.Contains("\"product:{id}\" of 'IProductReads.GetAsync'", diagnostic.GetMessage());
            Assert.Equal("\"product:{id}\"", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        }

        [Fact]
        public async Task A_CacheInvalidate_method_covers_the_tag()
        {
            var source = Usings + @"
public interface IProducts
{
    [Cache(Duration = ""10m"", Tags = [""product:{id}"", ""products""])]
    Task<Product> GetAsync(int id);

    [CacheInvalidate(Tags = [""product:{id}"", ""products""])]
    Task RenameAsync(int id, string name);
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_call_to_a_CacheInvalidate_method_of_another_project_covers_the_tag()
        {
            var library = CSharpCompilation.Create("Writes", [CSharpSyntaxTree.ParseText(@"
using System.Threading.Tasks;
using MethodCache;

public interface IProductWrites
{
    [CacheInvalidate(Tags = [""product:{id}""])]
    Task RenameAsync(int id, string name);
}")], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var source = Usings + Reads + @"
public sealed class Endpoint
{
    public Task RenameAsync(IProductWrites writes) => writes.RenameAsync(1, ""Hammer"");

    public async Task OrderAsync(ICacheManager cache) => await cache.InvalidateTagAsync(""orders"");
}";

            Assert.Empty(await AnalyzeAsync(source, library.ToMetadataReference()));
        }

        [Fact]
        public async Task A_CacheInvalidate_declared_in_a_referenced_project_counts_without_a_call_here()
        {
            // A shared invalidation interface in Common, called from another project than this one
            var common = CSharpCompilation.Create("Common", [CSharpSyntaxTree.ParseText(@"
using System.Threading.Tasks;
using MethodCache;

namespace Common.Caching
{
    public interface ICacheInvalidation
    {
        [CacheInvalidate(Tags = [""product:{id}""])]
        Task ProductChangedAsync(int id);
    }
}")], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            Assert.True(common.Emit(image, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(metadataOnly: true)).Success);
            var source = Usings + Reads + @"
public sealed class Endpoint
{
    public async Task OrderAsync(ICacheManager cache) => await cache.InvalidateTagAsync(""orders"");
}";

            Assert.Empty(await AnalyzeAsync(source, MetadataReference.CreateFromImage(image.ToArray())));
            Assert.NotEmpty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task Each_tag_nothing_invalidates_is_reported_on_its_own()
        {
            var source = Usings + @"
public interface IProducts
{
    [Cache(Duration = ""10m"", Tags = new[] { ""products"", ""product:{id}"", ""catalog"" })]
    Task<Product> GetAsync(int id);

    [CacheInvalidate(Tags = [""product:{id}""])]
    Task RenameAsync(int id, string name);
}";

            var diagnostics = await AnalyzeAsync(source);

            Assert.Equal(["\"products\"", "\"catalog\""], diagnostics
                .OrderBy(d => d.Location.SourceSpan.Start)
                .Select(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan)));
        }

        [Fact]
        public async Task A_project_that_invalidates_nothing_is_not_reported()
        {
            // Such as a project that only declares the cached interfaces, which the application invalidates
            Assert.Empty(await AnalyzeAsync(Usings + Reads));
        }

        [Fact]
        public async Task A_suppressed_tag_is_not_reported()
        {
            var source = Usings + @"
public interface IProducts
{
    // The worker invalidates it
#pragma warning disable MC0028
    [Cache(Duration = ""10m"", Tags = [""catalog""])]
#pragma warning restore MC0028
    Task<Product> GetAsync(int id);

    [CacheInvalidate(Tags = [""product:{id}""])]
    Task RenameAsync(int id, string name);
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_tag_invalidated_elsewhere_is_covered_on_every_method_that_has_it()
        {
            var source = Usings + @"
public interface IProducts
{
    // The import worker invalidates catalog
    [Cache(Duration = ""10m"", Tags = [""catalog"", ""product:{id}""], InvalidatedElsewhere = [""catalog""])]
    Task<Product> GetAsync(int id);

    [Cache(Duration = ""10m"", Tags = [""catalog""])]
    Task<Product[]> ListAsync();

    [CacheInvalidate(Tags = [""product:{id}""])]
    Task RenameAsync(int id, string name);
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_tag_invalidated_elsewhere_alone_does_not_make_the_project_one_that_invalidates()
        {
            var source = Usings + @"
public interface IProducts
{
    [Cache(Duration = ""10m"", Tags = [""catalog"", ""product:{id}""], InvalidatedElsewhere = [""catalog""])]
    Task<Product> GetAsync(int id);
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task An_invalidated_elsewhere_tag_that_the_method_does_not_have_is_reported()
        {
            var source = Usings + @"
public interface IProducts
{
    [Cache(Duration = ""10m"", Tags = [""catalog"", ""product:{id}""], InvalidatedElsewhere = [""catalogue"", ""product:{Id}""])]
    Task<Product> GetAsync(int id);

    [CacheInvalidate(Tags = [""product:{id}""])]
    Task RenameAsync(int id, string name);
}";

            var diagnostics = await AnalyzeAsync(source);

            // The misspelled tag covers nothing on the method, so catalog is still uninvalidated; the placeholder matches
            var unknown = Assert.Single(diagnostics, d => d.Id == "MC0029");
            Assert.Contains("\"catalogue\"", unknown.GetMessage());
            Assert.Equal("\"catalogue\"", unknown.Location.SourceTree!.GetText().ToString(unknown.Location.SourceSpan));
            Assert.Contains("\"catalog\"", Assert.Single(diagnostics, d => d.Id == "MC0028").GetMessage());
        }

        [Fact]
        public async Task Invalidated_elsewhere_without_tags_on_the_attribute_is_not_checked()
        {
            // The tags then come from configuration, which the analyzer can't read
            var source = Usings + @"
public interface IProducts
{
    [Cache(Group = ""catalog"", InvalidatedElsewhere = [""catalog""])]
    Task<Product> GetAsync(int id);
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        private static IEnumerable<MetadataReference> References()
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            return trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, params MetadataReference[] projects)
        {
            var compilation = CSharpCompilation.Create("TestAssembly",
                [CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(EntityFrameworkCore)], References().Concat(projects),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new UninvalidatedTagAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
