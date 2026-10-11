namespace MethodCache.Demo;

public sealed record Forecast(string City, int TemperatureC, string Summary, DateTimeOffset FetchedAt);

public sealed record UserProfile(int Id, string Name, string Email);

public sealed record Product(string Sku, string Name, decimal Price);

/// <summary>A slow weather API. One attribute caches it for five minutes.</summary>
public interface IWeatherService
{
    [Cache(Duration = "00:05:00", Tags = ["weather"])]
    Task<Forecast> GetForecastAsync(string city);
}

/// <summary>Profiles are tagged per user, so updating one user only invalidates that user.</summary>
public interface IUserProfiles
{
    [Cache(Duration = "00:10:00", Tags = ["users", "user:{userId}"])]
    Task<UserProfile> GetProfileAsync(int userId);

    [CacheInvalidate(Tags = ["user:{userId}"])]
    Task RenameAsync(int userId, string newName);
}

/// <summary>Has no duration of its own: it gets one from the global default, appsettings.json or a runtime override.</summary>
public interface IProductCatalog
{
    [Cache]
    Task<Product> GetProductAsync(string sku);

    [Cache(Duration = "00:00:30")]
    Task<decimal> GetPriceAsync(string sku);
}

/// <summary>Counts how often the real implementation of <typeparamref name="TService"/> runs, so the demo can prove what was cached.</summary>
public sealed class CallCounter<TService>
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Increment() => Interlocked.Increment(ref _calls);

    public void Reset() => Volatile.Write(ref _calls, 0);
}

public sealed class WeatherService(CallCounter<IWeatherService> counter) : IWeatherService
{
    private static readonly string[] Summaries = ["Sunny", "Cloudy", "Rain", "Snow", "Windy"];

    public async Task<Forecast> GetForecastAsync(string city)
    {
        counter.Increment();
        await Task.Delay(400); // A slow HTTP call
        return new Forecast(city, Random.Shared.Next(-5, 25), Summaries[Random.Shared.Next(Summaries.Length)], DateTimeOffset.Now);
    }
}

public sealed class UserProfiles(CallCounter<IUserProfiles> counter) : IUserProfiles
{
    private readonly Dictionary<int, string> _names = new() { [1] = "Ada Lovelace", [2] = "Alan Turing" };

    public async Task<UserProfile> GetProfileAsync(int userId)
    {
        counter.Increment();
        await Task.Delay(150); // A database query
        var name = _names[userId];
        return new UserProfile(userId, name, $"{name.Split(' ')[0].ToLowerInvariant()}@example.com");
    }

    public Task RenameAsync(int userId, string newName)
    {
        _names[userId] = newName;
        return Task.CompletedTask;
    }
}

public sealed class ProductCatalog : IProductCatalog
{
    public Task<Product> GetProductAsync(string sku) => Task.FromResult(new Product(sku, "Mechanical keyboard", 129m));

    public Task<decimal> GetPriceAsync(string sku) => Task.FromResult(129m);
}
