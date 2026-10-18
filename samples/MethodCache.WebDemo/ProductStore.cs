using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MethodCache.WebDemo;

/// <summary>A configurable demo database. SQLite transactions serialize writes from different processes.</summary>
public sealed class ProductStore
{
    private static readonly Product[] Seed =
    [
        new(1, "Mechanical keyboard", "keyboards", 129m),
        new(2, "Low-profile keyboard", "keyboards", 99m),
        new(3, "Ergonomic mouse", "mice", 79m),
        new(4, "4K monitor", "monitors", 449m),
    ];
    private readonly string _connectionString;
    private readonly Lazy<Task> _initialize;

    public ProductStore(IConfiguration configuration)
    {
        var file = Path.GetFullPath(configuration["Demo:DataFile"] ?? Path.Combine(Path.GetTempPath(), "methodcache-webdemo-products.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        _initialize = new Lazy<Task>(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS products (id INTEGER PRIMARY KEY, name TEXT NOT NULL, category TEXT NOT NULL, price TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync();
        using var transaction = connection.BeginTransaction();
        await SeedAsync(connection, transaction);
        transaction.Commit();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        await _initialize.Value;
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<Product?> GetAsync(int id)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, category, price FROM products WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Product>> GetByCategoryAsync(string category)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, category, price FROM products WHERE category = $category ORDER BY id";
        command.Parameters.AddWithValue("$category", category);
        await using var reader = await command.ExecuteReaderAsync();
        var products = new List<Product>();
        while (await reader.ReadAsync()) products.Add(Read(reader));
        return products;
    }

    public async Task<bool> UpdatePriceAsync(int id, decimal price)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE products SET price = $price WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$price", price.ToString(CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync() == 1;
    }

    public async Task ResetAsync()
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM products";
        await command.ExecuteNonQueryAsync();
        await SeedAsync(connection, transaction);
        transaction.Commit();
    }

    private static async Task SeedAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var product in Seed)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO products (id, name, category, price) VALUES ($id, $name, $category, $price)";
            command.Parameters.AddWithValue("$id", product.Id);
            command.Parameters.AddWithValue("$name", product.Name);
            command.Parameters.AddWithValue("$category", product.Category);
            command.Parameters.AddWithValue("$price", product.Price.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static Product Read(SqliteDataReader reader) => new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
}
