using System.Threading.Tasks;

namespace FrozenVendor.ThirdPartyNet10;

public interface IClient
{
    Task<int> ReadAsync(int id);
    Task<int> UncachedAsync(int id);
}

public sealed class Client : IClient
{
    public int Calls { get; private set; }
    public Task<int> ReadAsync(int id) => Task.FromResult(++Calls);
    public Task<int> UncachedAsync(int id) => Task.FromResult(++Calls);
}
