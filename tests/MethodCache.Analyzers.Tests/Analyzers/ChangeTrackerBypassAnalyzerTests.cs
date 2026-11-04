using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class ChangeTrackerBypassAnalyzerTests
    {
        // Stand-ins with EF Core's names, so the test needs no EF Core package
        private const string EntityFrameworkCore = @"
namespace Microsoft.EntityFrameworkCore
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class DbContext { public Infrastructure.DatabaseFacade Database => null!; public Task<int> SaveChangesAsync() => Task.FromResult(0); }
    public static class EntityFrameworkQueryableExtensions
    {
        public static Task<int> ExecuteUpdateAsync<T>(this IQueryable<T> source, Action<object> setters, CancellationToken ct = default) => Task.FromResult(0);
        public static int ExecuteDelete<T>(this IQueryable<T> source) => 0;
        public static Task<T> SingleAsync<T>(this IQueryable<T> source) => Task.FromResult(default(T)!);
    }
    public static class RelationalDatabaseFacadeExtensions
    {
        public static Task<int> ExecuteSqlRawAsync(this Infrastructure.DatabaseFacade database, string sql) => Task.FromResult(0);
        public static Task<int> ExecuteSqlInterpolatedAsync(this Infrastructure.DatabaseFacade database, FormattableString sql) => Task.FromResult(0);
    }
    public static class RelationalQueryableExtensions
    {
        public static IQueryable<T> FromSqlRaw<T>(this IQueryable<T> source, string sql) => source;
    }
}
namespace Microsoft.EntityFrameworkCore.Infrastructure { public class DatabaseFacade { } }
";

        private const string MethodCacheEntityFrameworkCore = @"
namespace Microsoft.EntityFrameworkCore
{
    public static class MethodCacheDbContextExtensions { public static void InvalidateAfterCommit(this DbContext context, params string[] tags) { } }
}
";

        private const string Usings = @"
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.EntityFrameworkCore;

public record Product(int Id, string Name);
";

        [Theory]
        [InlineData("await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { });", "ExecuteUpdateAsync")]
        [InlineData("products.Where(p => p.Id == id).ExecuteDelete();", "ExecuteDelete")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"update products set name = 'x'\");", "ExecuteSqlRawAsync")]
        [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"delete from products where id = {id}\");", "ExecuteSqlInterpolatedAsync")]
        public async Task Write_past_the_change_tracker_without_an_invalidation_is_reported(string write, string method)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Method(write)));

            Assert.Equal("MC0027", diagnostic.Id);
            Assert.Contains($"'{method}'", diagnostic.GetMessage());
            Assert.Equal(method, diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        }

        [Theory]
        [InlineData("await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { }); db.InvalidateAfterCommit(\"product:1\");")]
        [InlineData("await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { }); await cache.InvalidateTagsAsync(new[] { \"products\" });")]
        [InlineData("await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { }); await cache.InvalidateTagAsync(\"products\");")]
        [InlineData("products.Where(p => p.Id == id).ExecuteDelete(); await admin.ClearAsync();")]
        [InlineData("await Run(async () => await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { })); db.InvalidateAfterCommit(\"products\");")]
        [InlineData("await products.FromSqlRaw(\"select * from products\").SingleAsync();")]
        [InlineData("await db.SaveChangesAsync();")]
        public async Task Write_with_an_invalidation_or_a_read_is_not_reported(string body)
        {
            Assert.Empty(await AnalyzeAsync(Method(body)));
        }

        [Fact]
        public async Task An_invalidation_in_another_lambda_does_not_cover_the_write()
        {
            var body = "await Run(async () => await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { })); await Run(async () => db.InvalidateAfterCommit(\"products\"));";

            Assert.Single(await AnalyzeAsync(Method(body)));
        }

        [Fact]
        public async Task The_implementation_of_a_CacheInvalidate_method_is_not_reported()
        {
            var source = Usings + @"
public interface IProducts
{
    [CacheInvalidate(Tags = new[] { ""product:{id}"" })]
    Task RenameAsync(int id, string name);
}

public sealed class Products(DbContext db, IQueryable<Product> products) : IProducts
{
    public async Task RenameAsync(int id, string name) => await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { });
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_call_to_a_CacheInvalidate_method_covers_the_write()
        {
            var source = Usings + @"
public interface IProducts
{
    [CacheInvalidate(Tags = new[] { ""products"" })]
    Task ProductsChangedAsync();
}

public sealed class Endpoint(IProducts productCache)
{
    public async Task HandleAsync(IQueryable<Product> products)
    {
        await products.Where(p => p.Id == 1).ExecuteUpdateAsync(s => { });
        await productCache.ProductsChangedAsync();
    }
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task Nothing_is_reported_without_MethodCache_EntityFrameworkCore()
        {
            Assert.Empty(await AnalyzeAsync(Method("products.Where(p => p.Id == id).ExecuteDelete();"), withPackage: false));
        }

        private static string Method(string body) => Usings + @"
public sealed class Endpoint
{
    public async Task HandleAsync(int id, DbContext db, IQueryable<Product> products, ICacheManager cache, ICacheAdministrator admin)
    {
        " + body + @"
    }

    private static Task Run(Func<Task> action) => action();
}";

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool withPackage = true)
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(EntityFrameworkCore) };
            if (withPackage) trees.Add(CSharpSyntaxTree.ParseText(MethodCacheEntityFrameworkCore));
            var compilation = CSharpCompilation.Create("TestAssembly", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ChangeTrackerBypassAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
