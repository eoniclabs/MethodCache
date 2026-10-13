using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MethodCache.WebDemo;

public sealed record Product(int Id, string Name, string Category, decimal Price);

public interface IProductCatalog
{
    [Cache(Duration = "00:10:00", Tags = ["products", "product:{id}"])]
    Task<Product?> GetProductAsync(int id);

    [Cache(Duration = "00:02:00", Tags = ["products", "product-lists"])]
    Task<IReadOnlyList<Product>> GetByCategoryAsync(string category);

    // A price change makes this product and the lists stale. Other products stay cached.
    [CacheInvalidate(Tags = ["product:{id}", "product-lists"])]
    Task UpdatePriceAsync(int id, decimal price);
}

/// <summary>
/// A shared SQLite catalog: atomic updates and transactions work across both demo instances.
/// Queries still simulate a 300 ms database round trip to make cache hits visible.
/// </summary>
public sealed class ProductCatalog : IProductCatalog
{
    private static readonly TimeSpan QueryTime = TimeSpan.FromMilliseconds(300);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly Lazy<Task> _initialization;

    private static readonly Product[] Seed =
    [
        new(1, "Mechanical keyboard", "keyboards", 129m),
        new(2, "Low-profile keyboard", "keyboards", 99m),
        new(3, "Ergonomic mouse", "mice", 79m),
        new(4, "4K monitor", "monitors", 449m),
    ];

    public ProductCatalog(string? dataFile = null, TimeProvider? timeProvider = null)
    {
        var path = Path.GetFullPath(dataFile ?? Path.Combine(Path.GetTempPath(), "methodcache-webdemo-products.db"));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _initialization = new Lazy<Task>(InitializeAsync);
    }

    public async Task<Product?> GetProductAsync(int id) => (await QueryAsync("Id", id)).FirstOrDefault();

    public Task<IReadOnlyList<Product>> GetByCategoryAsync(string category) => QueryAsync("Category", category);

    public async Task UpdatePriceAsync(int id, decimal price)
    {
        await _initialization.Value;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Products SET Price = $price WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$price", price.ToString(CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync() == 0)
        {
            throw new KeyNotFoundException($"No product {id}");
        }
    }

    private async Task<IReadOnlyList<Product>> QueryAsync(string column, object value)
    {
        await Task.Delay(QueryTime, _timeProvider);
        await _initialization.Value;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // The column is one of the two constants above; only the value comes from a caller.
        command.CommandText = $"SELECT Id, Name, Category, Price FROM Products WHERE {column} = $value ORDER BY Id";
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync();
        var products = new List<Product>();
        while (await reader.ReadAsync())
        {
            products.Add(new Product(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3)));
        }
        return products;
    }

    private async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // TEXT preserves decimal prices exactly, rather than coercing them to SQLite's binary floating point.
        command.CommandText = "CREATE TABLE IF NOT EXISTS Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Category TEXT NOT NULL, Price TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync();
        foreach (var product in Seed)
        {
            command.CommandText = "INSERT OR IGNORE INTO Products (Id, Name, Category, Price) VALUES ($id, $name, $category, $price)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", product.Id);
            command.Parameters.AddWithValue("$name", product.Name);
            command.Parameters.AddWithValue("$category", product.Category);
            command.Parameters.AddWithValue("$price", product.Price.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
