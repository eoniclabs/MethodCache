using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class UncachedMemberNamesTests
{
    [Fact]
    public async Task KeywordPropertiesEventsAndIndexers_ForwardToImplementation()
    {
        var implementation = new Values();
        var services = new ServiceCollection();
        services.AddSingleton<IValues>(implementation);
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var values = provider.GetRequiredService<IValues>();
        var raised = 0;
        Action handler = () => raised++;
        values.@event += handler;
        values.@class = 42;
        values.@event -= handler;
        values.@class = 7;
        values[3] = 10;
        Assert.Equal(1, raised);
        Assert.Equal(7, values.@class);
        Assert.Equal(10, values[3]);
        Assert.Equal(10, implementation[3]);
        Assert.Equal("10", values.Get(values[3]));
        Assert.Equal("10", values.Get(values[3]));
        Assert.Equal(1, implementation.Loads);
    }

    public interface IValues
    {
        [Cache] string Get(int id);
        int @class { get; set; }
        event Action? @event;
        int this[int @namespace] { get; set; }
    }

    private sealed class Values : IValues
    {
        private readonly Dictionary<int, int> _values = new();
        private int _value;
        public int Loads;
        public string Get(int id) { Loads++; return id.ToString(); }
        public int @class { get => _value; set { _value = value; @event?.Invoke(); } }
        public event Action? @event;
        public int this[int @namespace] { get => _values[@namespace]; set => _values[@namespace] = value; }
    }
}
