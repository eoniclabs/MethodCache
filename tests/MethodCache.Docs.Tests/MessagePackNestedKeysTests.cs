using MethodCache.Extensibility;
using MethodCache.Serialization.MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class MessagePackNestedKeysTests
{
    [Fact]
    public void NestedDerivedData_GeneratedDecorator_DoesNotReturnAnotherArgumentsValue()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<IReader, Reader>();
        services.AddMethodCache();
        services.Replace(ServiceDescriptor.Singleton<ICacheKeyGenerator, MessagePackKeyGenerator>());
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IReader>();
        var first = new Envelope { Inner = new Derived { Id = 1, Value = 7 } };
        var second = new Envelope { Inner = new Derived { Id = 1, Value = 8 } };
        Assert.Equal(7, reader.Read(first));
        Assert.Equal(8, reader.Read(second));
        Assert.Equal(7, reader.Read(first));
    }

    public interface IReader { [Cache] int Read(Envelope envelope); }
    public sealed class Reader : IReader { public int Read(Envelope envelope) => ((Derived)envelope.Inner!).Value; }
    public class Base { public int Id { get; set; } }
    public sealed class Derived : Base { public int Value { get; set; } }
    public sealed class Envelope { public Base? Inner { get; set; } }
}
