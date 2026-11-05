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
    public class DbSet<T> where T : class { }
    public class ModelBuilder { public Metadata.Builders.EntityTypeBuilder<T> Entity<T>() where T : class => null!; }
    public static class RelationalEntityTypeBuilderExtensions
    {
        public static Metadata.Builders.EntityTypeBuilder<T> ToTable<T>(this Metadata.Builders.EntityTypeBuilder<T> builder, string name) where T : class => builder;
    }
}
namespace Microsoft.EntityFrameworkCore.Infrastructure { public class DatabaseFacade { } }
namespace Microsoft.EntityFrameworkCore.Metadata.Builders { public class EntityTypeBuilder<T> where T : class { } }
";

        private const string MethodCacheEntityFrameworkCore = @"
namespace Microsoft.EntityFrameworkCore
{
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
    public static class EntityFrameworkCoreMethodCacheBuilderExtensions
    {
        public static object UseEntityFrameworkCore<TContext>(this object builder, System.Action<EntityFrameworkCoreOptions> configure) => builder;
    }
}
";

        // A startup file in the same project, apart from the writes
        private static string Mapping(string mapping) => @"
using MethodCache.EntityFrameworkCore;

public static class Startup
{
    public static void Map(EntityFrameworkCoreOptions ef) => ef" + mapping + @";
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
public record DiscountedProduct(int Id, string Name, int Percent) : Product(Id, Name);
public record RefreshToken(int Id, string Value);
public record User(int Id, string Name);
public record Category(int Id);
[System.ComponentModel.DataAnnotations.Schema.Table(""stock_levels"")]
public record Stock(int Id);
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

        [Theory]
        [InlineData(".Entity<Product>(\"products\")", "tokens.Where(t => t.Id == id).ExecuteDelete();")]
        [InlineData(".Entity<Product>(\"products\")", "await tokens.Where(t => t.Id == id).ExecuteUpdateAsync(s => { });")]
        [InlineData(".Entity<User>(u => u.WhenDeleted(\"publications\"))", "await users.Where(u => u.Id == id).ExecuteUpdateAsync(s => { });")]
        [InlineData(".Entity<User>(u => u.WhenDeleted(\"publications\"))", "tokens.Where(t => t.Id == id).ExecuteDelete();")]
        public async Task A_write_to_a_type_without_tags_is_not_reported(string mapping, string write)
        {
            Assert.Empty(await AnalyzeAsync(Method(write), mapping: mapping));
        }

        [Theory]
        [InlineData(".Entity<Product>(\"products\")", "products.Where(p => p.Id == id).ExecuteDelete();")]
        [InlineData(".Entity<Product>(\"products\")", "await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { });")]
        [InlineData(".Entity<Product>(\"products\")", "discounted.Where(p => p.Id == id).ExecuteDelete();")]
        [InlineData(".Entity<DiscountedProduct>(\"products\")", "products.Where(p => p.Id == id).ExecuteDelete();")]
        [InlineData(".Entity<Product>(p => p.Tags(\"products\"))", "await products.Where(p => p.Id == id).ExecuteUpdateAsync(s => { });")]
        [InlineData(".Entity<User>(u => u.WhenDeleted(\"publications\"))", "users.Where(u => u.Id == id).ExecuteDelete();")]
        [InlineData(".Entity<User>(u => u.Tags(\"user:{Id}\").WhenDeleted(\"publications\"))", "await users.Where(u => u.Id == id).ExecuteUpdateAsync(s => { });")]
        [InlineData(".Entity<User>(u => Configure(u))", "await users.Where(u => u.Id == id).ExecuteUpdateAsync(s => { });")]
        public async Task A_write_to_a_mapped_type_is_reported(string mapping, string write)
        {
            var source = Method(write) + @"
public static class Builders
{
    public static void Configure<T>(MethodCache.EntityFrameworkCore.EntityTagsBuilder<T> builder) where T : class { }
}";
            var mappingWithHelper = mapping.Replace("Configure(", "Builders.Configure(", StringComparison.Ordinal);

            Assert.Equal("MC0027", Assert.Single(await AnalyzeAsync(source, mapping: mappingWithHelper)).Id);
        }

        [Fact]
        public async Task A_mapping_through_a_type_parameter_reports_every_write()
        {
            var source = Method("tokens.Where(t => t.Id == id).ExecuteDelete();") + @"
public static class Generic
{
    public static void Map<T>(MethodCache.EntityFrameworkCore.EntityFrameworkCoreOptions ef) where T : class => ef.Entity<T>(""all"");
}";

            Assert.Single(await AnalyzeAsync(source, mapping: ".Entity<Product>(\"products\")"));
        }

        [Theory]
        [InlineData("update products set name = 'x'")]
        [InlineData("UPDATE \\\"Product\\\" SET \\\"Name\\\" = 'x'")]
        [InlineData("delete from public.product where id = 1")]
        [InlineData("delete from [dbo].[Products]")]
        [InlineData("insert into products (name) values ('x') on conflict (name) do nothing")]
        [InlineData("set local lock_timeout = '5s'; update products set name = 'x'")]
        [InlineData("call recalculate_prices()")]
        [InlineData("exec dbo.RecalculatePrices")]
        [InlineData("do $$ begin perform 1; end $$")]
        [InlineData("update categories set id = 2")]
        [InlineData("truncate stock_levels")]
        public async Task Raw_SQL_that_names_a_mapped_table_or_runs_code_is_reported(string sql)
        {
            var write = $"await db.Database.ExecuteSqlRawAsync(\"{sql}\");";

            Assert.Equal("MC0027", Assert.Single(await AnalyzeAsync(Method(write),
                mapping: ".Entity<Product>(\"products\").Entity<Category>(\"categories\").Entity<Stock>(\"stock\")")).Id);
        }

        [Theory]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"delete from refresh_tokens\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"SET LOCAL lock_timeout = '5s'\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"set local lock_timeout = '5s'; select pg_advisory_xact_lock(1)\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"insert into shared_cache (key, value) values ('products', 'x') on conflict (key) do update set value = excluded.value\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"-- clears old products\\n delete from login_attempts where at < now()\");")]
        [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"delete from login_attempts where user_name = {sql}\");")]
        public async Task Raw_SQL_that_names_no_mapped_table_is_not_reported(string write)
        {
            Assert.Empty(await AnalyzeAsync(Method(write), mapping: ".Entity<Product>(\"products\")"));
        }

        [Theory]
        [InlineData("await db.Database.ExecuteSqlRawAsync(sql);")]
        [InlineData("await db.Database.ExecuteSqlRawAsync($\"delete from {sql}\");")]
        public async Task Raw_SQL_that_cannot_be_read_is_reported_also_with_a_mapping(string write)
        {
            Assert.Single(await AnalyzeAsync(Method(write), mapping: ".Entity<Product>(\"products\")"));
        }

        [Theory]
        [InlineData("delete from catalog_items", true)]
        [InlineData("delete from inventory", true)]
        [InlineData("delete from access_tokens", false)]
        public async Task Raw_SQL_is_checked_against_the_names_the_context_gives_the_tables(string sql, bool reported)
        {
            var source = Method($"await db.Database.ExecuteSqlRawAsync(\"{sql}\");") + @"
public sealed class ShopDb : DbContext
{
    public DbSet<Product> Inventory { get; } = null!;
    public DbSet<RefreshToken> Tokens { get; } = null!;

    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().ToTable(""catalog_items"");
        modelBuilder.Entity<RefreshToken>().ToTable(""access_tokens"");
    }
}";

            Assert.Equal(reported ? 1 : 0, (await AnalyzeAsync(source, mapping: ".Entity<Product>(\"products\")")).Length);
        }

        [Fact]
        public async Task Raw_SQL_is_checked_against_the_DbSets_of_a_context_from_another_project()
        {
            // The context and its entity are in a data project; the startup project connects it and writes raw SQL
            var entityFrameworkCore = CSharpCompilation.Create("EntityFrameworkCore",
                [CSharpSyntaxTree.ParseText(EntityFrameworkCore), CSharpSyntaxTree.ParseText(MethodCacheEntityFrameworkCore)], References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).ToMetadataReference();
            var data = CSharpCompilation.Create("Data", [CSharpSyntaxTree.ParseText(@"
namespace Data
{
    public record Item(int Id);
    public class ShopDb : Microsoft.EntityFrameworkCore.DbContext { public Microsoft.EntityFrameworkCore.DbSet<Item> Stock { get; } = null!; }
}")], References().Append(entityFrameworkCore), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).ToMetadataReference();
            var app = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(Method("await db.Database.ExecuteSqlRawAsync(\"delete from stock\");") + @"
public static class Startup
{
    public static void Configure(object mc) => MethodCache.EntityFrameworkCore.EntityFrameworkCoreMethodCacheBuilderExtensions
        .UseEntityFrameworkCore<Data.ShopDb>(mc, ef => ef.Entity<Data.Item>(""items""));
}")], References().Append(entityFrameworkCore).Append(data), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(app.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

            var diagnostics = await app.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ChangeTrackerBypassAnalyzer())).GetAnalyzerDiagnosticsAsync();

            Assert.Equal("MC0027", Assert.Single(diagnostics).Id);
        }

        [Theory]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"select pg_advisory_xact_lock(42)\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"  -- one writer at a time\\n SELECT pg_advisory_xact_lock(42);\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"/* lock */ select 1\");")]
        [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"select pg_advisory_xact_lock({id})\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"SET LOCAL lock_timeout = '5s'\");")]
        public async Task Raw_SQL_that_only_selects_or_sets_is_not_reported(string read)
        {
            Assert.Empty(await AnalyzeAsync(Method(read)));
        }

        [Theory]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"select 1; delete from products\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"with gone as (delete from products returning id) select count(*) from gone\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"selected_products_cleanup()\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(sql);")]
        [InlineData("await db.Database.ExecuteSqlRawAsync($\"select 1 {sql}\");")]
        public async Task Raw_SQL_that_may_write_is_reported(string write)
        {
            Assert.Single(await AnalyzeAsync(Method(write)));
        }

        [Fact]
        public async Task A_call_to_an_InvalidatesCache_method_covers_the_write()
        {
            var source = Usings + @"
public static class CacheChanges
{
    [InvalidatesCache]
    public static void Changed(this DbContext db, string tag) { }
}

public sealed class Endpoint
{
    public async Task HandleAsync(DbContext db, IQueryable<Product> products)
    {
        await products.Where(p => p.Id == 1).ExecuteUpdateAsync(s => { });
        db.Changed(""products"");
    }
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_call_to_a_helper_without_InvalidatesCache_does_not_cover_the_write()
        {
            var source = Usings + @"
public static class CacheChanges
{
    public static void Changed(this DbContext db, string tag) { }
}

public sealed class Endpoint
{
    public async Task HandleAsync(DbContext db, IQueryable<Product> products)
    {
        await products.Where(p => p.Id == 1).ExecuteUpdateAsync(s => { });
        db.Changed(""products"");
    }
}";

            Assert.Single(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task An_InvalidatesCache_interface_method_counts_through_its_implementation()
        {
            var source = Usings + @"
public interface ICacheChanges
{
    [InvalidatesCache]
    Task ChangedAsync(string tag);
}

public sealed class CacheChanges : ICacheChanges
{
    public Task ChangedAsync(string tag) => Task.CompletedTask;
}

public sealed class Endpoint
{
    public async Task HandleAsync(CacheChanges changes, IQueryable<Product> products)
    {
        await products.Where(p => p.Id == 1).ExecuteUpdateAsync(s => { });
        await changes.ChangedAsync(""products"");
    }
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_generic_InvalidatesCache_interface_method_counts_through_its_implementation()
        {
            var source = Usings + @"
public interface ICacheChanges
{
    [InvalidatesCache]
    Task ChangedAsync<TKey>(TKey key);
}

public sealed class CacheChanges<TScope> : ICacheChanges
{
    public Task ChangedAsync<TKey>(TKey key) => Task.CompletedTask;
}

public sealed class Endpoint
{
    public async Task HandleAsync(CacheChanges<Endpoint> changes, IQueryable<Product> products)
    {
        await products.Where(p => p.Id == 1).ExecuteUpdateAsync(s => { });
        await changes.ChangedAsync(42);
    }
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_write_inside_an_explicit_implementation_of_an_InvalidatesCache_method_is_not_reported()
        {
            var source = Usings + @"
public interface IPublications
{
    [InvalidatesCache]
    Task RevokeAsync(IQueryable<Product> products);
}

public sealed class Publications : IPublications
{
    async Task IPublications.RevokeAsync(IQueryable<Product> products)
    {
        await products.Where(p => p.Id == 1).ExecuteUpdateAsync(s => { });
    }
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task A_write_inside_an_InvalidatesCache_method_is_not_reported()
        {
            var source = Usings + @"
public sealed class Publications
{
    [InvalidatesCache]
    public async Task RevokeAsync(IQueryable<Product> products)
    {
        await products.Where(p => p.Id == 1).ExecuteUpdateAsync(s => { });
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
    public async Task HandleAsync(int id, DbContext db, IQueryable<Product> products, ICacheManager cache, ICacheAdministrator admin,
        IQueryable<DiscountedProduct> discounted, IQueryable<RefreshToken> tokens, IQueryable<User> users, string sql)
    {
        " + body + @"
    }

    private static Task Run(Func<Task> action) => action();
}";

        private static IEnumerable<MetadataReference> References()
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            return trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool withPackage = true, string? mapping = null)
        {
            var references = References();
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(EntityFrameworkCore) };
            if (withPackage) trees.Add(CSharpSyntaxTree.ParseText(MethodCacheEntityFrameworkCore));
            if (mapping != null) trees.Add(CSharpSyntaxTree.ParseText(Mapping(mapping)));
            var compilation = CSharpCompilation.Create("TestAssembly", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ChangeTrackerBypassAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
