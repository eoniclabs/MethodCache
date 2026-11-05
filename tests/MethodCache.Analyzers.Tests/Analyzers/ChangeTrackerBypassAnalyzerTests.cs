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
        [InlineData("const string Table = \"products\"; await db.Database.ExecuteSqlRawAsync($\"delete from {Table} where id = {id}\");", true)]
        [InlineData("const string Table = \"refresh_tokens\"; await db.Database.ExecuteSqlRawAsync($\"delete from {Table} where id = {id}\");", false)]
        public async Task ExecuteSqlRaw_reads_constants_and_numbers_in_the_SQL(string write, bool reported)
        {
            Assert.Equal(reported ? 1 : 0, (await AnalyzeAsync(Method(write), mapping: ".Entity<Product>(\"products\")")).Length);
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
        [InlineData("tokens.Where(t => t.Id == 1).ExecuteDelete();", false)]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"delete from refresh_tokens\");", false)]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"update audit_log set seen = true\");", false)]
        [InlineData("products.Where(p => p.Id == 1).ExecuteDelete();", true)]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"delete from products\");", true)]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"delete from catalog_items\");", true)]
        public async Task A_test_project_reads_the_mapping_the_generator_wrote_into_the_project_it_tests(string write, bool reported)
        {
            var tests = ProjectThatReferencesApi(write, generateApi: true, out var generated);

            Assert.Contains("entity:any:Api.Product", generated);
            var diagnostics = await tests.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ChangeTrackerBypassAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.Equal(reported ? 1 : 0, diagnostics.Length);
        }

        [Theory]
        [InlineData("ef => ef.Entity<RefreshToken>(t => t.WhenDeleted(\"sessions\"))", "tokens.Where(t => t.Id == 1).ExecuteDelete();", true)]
        [InlineData("ef => ef.Entity<RefreshToken>(t => t.WhenDeleted(\"sessions\"))", "await tokens.ExecuteUpdateAsync(s => { });", false)]
        [InlineData("ef => { void Map<T>() where T : class => ef.Entity<T>(\"all\"); Map<RefreshToken>(); }", "await tokens.ExecuteUpdateAsync(s => { });", true)]
        [InlineData("ef => { ef.Entity<Product>(\"products\"); ef.Entity<System.Collections.Generic.List<int>>(\"lists\"); }", "await tokens.ExecuteUpdateAsync(s => { });", true)]
        public async Task A_test_project_reads_what_the_mapping_in_the_project_it_tests_covers(string mapping, string write, bool reported)
        {
            var tests = ProjectThatReferencesApi(write, generateApi: true, out _, mapping);

            var diagnostics = await tests.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ChangeTrackerBypassAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.Equal(reported ? 1 : 0, diagnostics.Length);
        }

        [Fact]
        public async Task Without_the_mapping_in_the_referenced_project_every_write_counts()
        {
            var tests = ProjectThatReferencesApi("tokens.Where(t => t.Id == 1).ExecuteDelete();", generateApi: false, out _);

            var diagnostics = await tests.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ChangeTrackerBypassAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.Equal("MC0027", Assert.Single(diagnostics).Id);
        }

        [Fact]
        public void The_generator_writes_no_mapping_into_a_project_that_maps_nothing()
        {
            var compilation = CSharpCompilation.Create("App",
                [CSharpSyntaxTree.ParseText(EntityFrameworkCore), CSharpSyntaxTree.ParseText(MethodCacheEntityFrameworkCore), CSharpSyntaxTree.ParseText(@"
public class ShopDb : Microsoft.EntityFrameworkCore.DbContext { }
public static class Startup
{
    public static void Configure(object mc) => MethodCache.EntityFrameworkCore.EntityFrameworkCoreMethodCacheBuilderExtensions
        .UseEntityFrameworkCore<ShopDb>(mc, ef => { });
}")], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var result = CSharpGeneratorDriver.Create(new SourceGenerator.MethodCacheGenerator()).RunGenerators(compilation).GetRunResult();

            Assert.DoesNotContain(result.GeneratedTrees, tree => tree.FilePath.EndsWith("MethodCacheEntityMapping.g.cs", StringComparison.Ordinal));
        }

        /// <summary>
        /// An API project that maps <c>Product</c> and connects its context, and a test project that references it and
        /// writes. The API is built as a reference assembly, so the test project sees only its public metadata.
        /// </summary>
        private static CSharpCompilation ProjectThatReferencesApi(string write, bool generateApi, out string generated,
            string mapping = "ef => ef.Entity<Product>(\"products\")")
        {
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            var entityFrameworkCore = Image(CSharpCompilation.Create("Microsoft.EntityFrameworkCore",
                [CSharpSyntaxTree.ParseText(EntityFrameworkCore)], References(), options));
            var package = Image(CSharpCompilation.Create("MethodCache.EntityFrameworkCore",
                [CSharpSyntaxTree.ParseText(MethodCacheEntityFrameworkCore)], References().Append(entityFrameworkCore), options));
            Compilation api = CSharpCompilation.Create("Api", [CSharpSyntaxTree.ParseText(@"
namespace Api
{
    using Microsoft.EntityFrameworkCore;

    public record Product(int Id);
    public record RefreshToken(int Id);
    public record AuditEntry(int Id);
    public class ShopDb : DbContext
    {
        public DbSet<Product> Products { get; } = null!;
        public DbSet<RefreshToken> RefreshTokens { get; } = null!;
        public DbSet<AuditEntry> AuditLog { get; } = null!;

        public void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Product>().ToTable(""catalog_items"");
    }
    public static class Startup
    {
        public static void Configure(object mc) => MethodCache.EntityFrameworkCore.EntityFrameworkCoreMethodCacheBuilderExtensions
            .UseEntityFrameworkCore<ShopDb>(mc, " + mapping + @");
    }
}")], References().Append(entityFrameworkCore).Append(package), options);
            generated = "";
            if (generateApi)
            {
                CSharpGeneratorDriver.Create(new SourceGenerator.MethodCacheGenerator()).RunGeneratorsAndUpdateCompilation(api, out api, out _);
                generated = string.Join("\n", api.SyntaxTrees.Skip(1).Select(tree => tree.ToString()));
            }

            var tests = CSharpCompilation.Create("Api.Tests", [CSharpSyntaxTree.ParseText(@"
using System.Linq;
using System.Threading.Tasks;
using Api;
using Microsoft.EntityFrameworkCore;

public sealed class Fixture
{
    public async Task ArrangeAsync(ShopDb db, IQueryable<Product> products, IQueryable<RefreshToken> tokens)
    {
        " + write + @"
    }
}")], References().Append(entityFrameworkCore).Append(package).Append(Image(api)), options);
            Assert.Empty(tests.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return tests;
        }

        // A reference assembly, as a build passes a referenced project to the compiler: no method bodies, so it
        // references only the assemblies its public signatures use
        private static MetadataReference Image(Compilation compilation)
        {
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(metadataOnly: true, includePrivateMembers: false));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return MetadataReference.CreateFromImage(stream.ToArray());
        }

        [Theory]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"select pg_advisory_xact_lock(42)\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"  -- one writer at a time\\n SELECT pg_advisory_xact_lock(42);\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"/* lock */ select 1\");")]
        [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"select pg_advisory_xact_lock({id})\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync(\"SET LOCAL lock_timeout = '5s'\");")]
        [InlineData("await db.Database.ExecuteSqlRawAsync($\"SET LOCAL lock_timeout = '{id}ms'\");")]
        [InlineData("const long LockId = 42; await db.Database.ExecuteSqlRawAsync($\"SELECT pg_advisory_xact_lock({LockId})\");")]
        [InlineData("long? key = id; await db.Database.ExecuteSqlRawAsync($\"select pg_advisory_xact_lock({key:D})\");")]
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
        [InlineData("await db.Database.ExecuteSqlRawAsync($\"SET LOCAL lock_timeout = '{sql}'\");")]
        [InlineData("const string Statement = \"delete from products\"; await db.Database.ExecuteSqlRawAsync($\"select pg_advisory_xact_lock({id}); {Statement}\");")]
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
