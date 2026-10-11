namespace MethodCache.SourceGenerator.IntegrationTests.Vendor;

/// <summary>A vendor interface with members besides methods, which the decorator must pass through.</summary>
public interface IVendorSession
{
    string Region { get; set; }

    string this[int index] { get; }

    event EventHandler? Changed;
}

/// <summary>Two SDKs that both nest an interface named IClient.</summary>
public static class SdkOne
{
    public interface IClient : IVendorSession
    {
        Task<int> GetAsync(int id);
    }
}

public static class SdkTwo
{
    public interface IClient
    {
        Task<int> GetAsync(int id);
    }
}

/// <summary>Can't be a service type: static abstract members can only be called through a type parameter.</summary>
public interface IStaticVendorClient
{
    static abstract IStaticVendorClient Create();

    Task<int> GetAsync(int id);
}

public sealed class SessionVendorClient : SdkOne.IClient, SdkTwo.IClient
{
    private int _calls;

    public int Calls => _calls;

    public string Region { get; set; } = "eu";

    public string this[int index] => "item" + index;

    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<int> GetAsync(int id)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(id * 10);
    }
}
