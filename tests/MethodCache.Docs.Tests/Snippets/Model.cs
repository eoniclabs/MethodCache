namespace Docs;

// Types the examples use. Not shown in the docs.
public sealed record User(int Id, string Name);

public sealed record ExchangeRate(string From, string To, decimal Rate);

public sealed record Forecast(string City, int TemperatureC);

public interface IUserDatabase
{
    Task<User> LoadUserAsync(int id);

    Task RenameAsync(int id, string name);
}

public sealed class InMemoryUserDatabase : IUserDatabase
{
    private readonly Dictionary<int, string> _names = new() { [1] = "Ada Lovelace", [2] = "Alan Turing", [42] = "Grace Hopper" };

    public int Loads { get; private set; }

    public Task<User> LoadUserAsync(int id)
    {
        Loads++;
        return Task.FromResult(new User(id, _names[id]));
    }

    public Task RenameAsync(int id, string name)
    {
        _names[id] = name;
        return Task.CompletedTask;
    }
}
