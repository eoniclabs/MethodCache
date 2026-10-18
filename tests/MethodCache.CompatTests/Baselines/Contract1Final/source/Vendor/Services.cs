using MethodCache;
using FrozenVendor.ThirdPartyFinal;

[assembly: CacheInterface(typeof(IClient), nameof(IClient.ReadAsync))]

namespace FrozenVendor.Contract1Final;

public sealed class Marker;
public sealed record Query(string Name, List<int> Values);
public sealed record Point(int X, string Name);
public sealed record Token(string CacheKeyPart) : ICacheKeyProvider;

public interface IService
{
    [Cache(Tags = new[] { "vendor:{id}" })] int Read(int id);
    [Cache(Tags = new[] { "vendor:{id}" })] Task<int> ReadAsync(int id);
    [Cache(Tags = new[] { "vendor:{id}" })] ValueTask<int> ReadValueAsync(int id);
    [Cache] Task<int> SearchAsync(Query query);
    [Cache(Version = 2)] Task<T> EchoAsync<T>(T value);
    [Cache] int Collection(int[] values);
    [Cache] int Record(Point value);
    [Cache] int Provider(Token value);
    [CacheInvalidate(Tags = new[] { "vendor:{id}" })] Task UpdateAsync(int id);
}

public sealed class Service : IService
{
    public int Calls { get; private set; }
    public int Read(int id) => ++Calls;
    public Task<int> ReadAsync(int id) => Task.FromResult(++Calls);
    public ValueTask<int> ReadValueAsync(int id) => new(++Calls);
    public Task<int> SearchAsync(Query query) => Task.FromResult(++Calls);
    public Task<T> EchoAsync<T>(T value) { Calls++; return Task.FromResult(value); }
    public int Collection(int[] values) => ++Calls;
    public int Record(Point value) => ++Calls;
    public int Provider(Token value) => ++Calls;
    public Task UpdateAsync(int id) => Task.CompletedTask;
}

public interface IRepository<T>
{
    [Cache] Task<T> ReadAsync(int id);
}

public sealed class StringRepository : IRepository<string>
{
    public int Calls { get; private set; }
    public Task<string> ReadAsync(int id) => Task.FromResult((++Calls).ToString(System.Globalization.CultureInfo.InvariantCulture));
}
