using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MethodCache.PostgreSql.IntegrationTests;

/// <summary>
/// MethodCache.EntityFrameworkCore against a real PostgreSQL: one process writes through EF Core, and the
/// invalidation that follows the commit reaches the other process's memory cache.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public class EntityFrameworkCoreTests(PostgreSqlFixture postgres)
{
    private const string Schema = "methodcache_ef_tests";

    public sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class ItemsDb(DbContextOptions<ItemsDb> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Item>().ToTable("items", Schema);
    }

    public interface IItemNames
    {
        [Cache(Duration = "1h", Tags = ["item:{id}"])]
        Task<string?> GetAsync(int id);
    }

    public sealed class ItemNames(IServiceScopeFactory scopes) : IItemNames
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<string?> GetAsync(int id)
        {
            Interlocked.Increment(ref _calls);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ItemsDb>();
            return await db.Items.Where(i => i.Id == id).Select(i => i.Name).SingleOrDefaultAsync();
        }
    }

    [SkippableFact]
    public async Task A_commit_in_one_process_invalidates_the_other_processes()
    {
        var options = postgres.Options("efcore");
        try
        {
            var id = await CreateItemAsync(options.ConnectionString, "Before");
            await using var writer = await StartAsync(options);
            await using var reader = await StartAsync(options);
            var names = reader.GetRequiredService<IItemNames>();
            Assert.Equal("Before", await names.GetAsync(id));

            await using (var scope = writer.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ItemsDb>();
                await using var transaction = await db.Database.BeginTransactionAsync();
                (await db.Items.SingleAsync(i => i.Id == id)).Name = "After";
                await db.SaveChangesAsync();
                Assert.Equal("Before", await names.GetAsync(id));
                await transaction.CommitAsync();
            }

            Assert.Equal("After", await EventuallyAsync(() => names.GetAsync(id), "After"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    /// <summary>
    /// The recipe in docs/features/distributed-cache.md, "A worker and an API": values stay in each process's memory,
    /// the worker caches nothing and only writes through EF Core, and its save reaches the API's memory cache.
    /// </summary>
    [SkippableFact]
    public async Task A_worker_save_invalidates_the_apis_memory_only_cache()
    {
        var options = postgres.Options("efworker");
        try
        {
            var id = await CreateItemAsync(options.ConnectionString, "Before");
            await using var worker = await StartAsync(options, memoryOnly: true, cachedReads: false);
            await using var api = await StartAsync(options, memoryOnly: true, cachedReads: true);
            var names = api.GetRequiredService<IItemNames>();
            Assert.Equal("Before", await names.GetAsync(id));
            Assert.Equal("Before", await names.GetAsync(id));
            Assert.Equal(1, api.GetRequiredService<ItemNames>().Calls);   // the second read came from the API's memory

            await using (var scope = worker.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ItemsDb>();
                (await db.Items.SingleAsync(i => i.Id == id)).Name = "After";
                await db.SaveChangesAsync();
            }

            Assert.Equal("After", await EventuallyAsync(() => names.GetAsync(id), "After"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task An_ambient_transaction_invalidates_the_other_processes_when_it_commits()
    {
        var options = postgres.Options("efambient");
        try
        {
            var id = await CreateItemAsync(options.ConnectionString, "Before");
            await using var writer = await StartAsync(options);
            await using var reader = await StartAsync(options);
            var names = reader.GetRequiredService<IItemNames>();
            Assert.Equal("Before", await names.GetAsync(id));

            using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await using var scope = writer.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ItemsDb>();
                (await db.Items.SingleAsync(i => i.Id == id)).Name = "After";
                await db.SaveChangesAsync();
                transaction.Complete();
            }

            Assert.Equal("After", await EventuallyAsync(() => names.GetAsync(id), "After"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    public sealed class Note
    {
        public int Id { get; set; }
        public int Owner { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class NotesDb(DbContextOptions<NotesDb> options) : DbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Note>().ToTable("notes");
    }

    /// <summary>Sets up each connection for one user, as an app with row-level security does.</summary>
    private sealed class SignedIn(string role, string schema, int user) : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"set role {role}; set search_path to {schema}; select set_config('app.user', '{user}', false);";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    [SkippableFact]
    public async Task A_row_that_row_level_security_hides_is_not_changed_by_a_save_that_did_not_load_it()
    {
        var options = postgres.Options("efrls");
        var role = options.SchemaName + "_user";
        try
        {
            // Users read only their own notes, and may update and delete any: the save that didn't load the row reads
            // it for its stored values with the user's permissions, and finds nothing
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"""
                create schema {options.SchemaName};
                create table {options.SchemaName}.notes ("Id" int primary key, "Owner" int not null, "Text" text not null);
                insert into {options.SchemaName}.notes values (1, 1, 'Alice''s'), (2, 2, 'Bob''s');
                alter table {options.SchemaName}.notes enable row level security;
                create policy read_own on {options.SchemaName}.notes for select using ("Owner" = current_setting('app.user')::int);
                create policy update_any on {options.SchemaName}.notes for update using (true) with check (true);
                create policy delete_any on {options.SchemaName}.notes for delete using (true);
                create role {role};
                grant usage on schema {options.SchemaName} to {role};
                grant select, update, delete on {options.SchemaName}.notes to {role};
                """);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<NotesDb>(o => o.UseNpgsql(options.ConnectionString).AddInterceptors(new SignedIn(role, options.SchemaName, user: 1)));
            services.AddMethodCache(mc => mc.UseEntityFrameworkCore<NotesDb>(ef => ef.Entity<Note>("note:{Id}", "notes:{Owner}")));
            await using var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NotesDb>();
                db.Notes.Update(new Note { Id = 1, Owner = 1, Text = "Changed" });
                await db.SaveChangesAsync();
            }

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NotesDb>();
                db.Notes.Update(new Note { Id = 2, Owner = 1, Text = "Mine now" });
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            }

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NotesDb>();
                db.Notes.Remove(new Note { Id = 2 });
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            }

            Assert.Equal("Changed", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString, $"select \"Text\" from {options.SchemaName}.notes where \"Id\" = 1"));
            Assert.Equal("Bob's", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString, $"select \"Text\" from {options.SchemaName}.notes where \"Id\" = 2"));
        }
        finally
        {
            await PostgreSqlFixture.DropSchemaAsync(options);
            if (!string.IsNullOrEmpty(options.ConnectionString))
            {
                await PostgreSqlFixture.ExecuteAsync(options.ConnectionString,
                    $"do $$ begin if exists (select from pg_roles where rolname = '{role}') then drop owned by {role}; drop role {role}; end if; end $$;");
            }
        }
    }

    private static async Task<string?> EventuallyAsync(Func<Task<string?>> read, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? value;
        while ((value = await read()) != expected) await Task.Delay(50, deadline.Token);
        return value;
    }

    private static async Task<int> CreateItemAsync(string connectionString, string name)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand(
            $"create schema if not exists {Schema}; create table if not exists {Schema}.items (\"Id\" int generated by default as identity primary key, \"Name\" text not null);",
            connection))
        {
            await create.ExecuteNonQueryAsync();
        }
        await using var insert = new NpgsqlCommand($"insert into {Schema}.items (\"Name\") values (@name) returning \"Id\";", connection);
        insert.Parameters.AddWithValue("name", name);
        return (int)(await insert.ExecuteScalarAsync())!;
    }

    private static async Task<ServiceProvider> StartAsync(PostgreSqlOptions options, bool memoryOnly = false, bool cachedReads = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ItemsDb>(o => o.UseNpgsql(options.ConnectionString));
        if (cachedReads)
        {
            services.AddSingleton<ItemNames>();
            services.AddSingleton<IItemNames>(provider => provider.GetRequiredService<ItemNames>());
        }
        services.AddMethodCache(mc =>
        {
            mc.UsePostgreSql(o =>
                {
                    o.ConnectionString = options.ConnectionString;
                    o.SchemaName = options.SchemaName;
                    o.BackplanePollingInterval = TimeSpan.FromMilliseconds(50);
                })
                .UseEntityFrameworkCore<ItemsDb>(ef => ef.Entity<Item>("item:{Id}"));
            if (memoryOnly) mc.Defaults(o => o.WithDistributed(false));
        });
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await PostgreSqlFixture.WaitForTablesAsync(hosted, deadline.Token);
        return provider;
    }
}
