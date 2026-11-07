using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace MethodCache.EntityFrameworkCore.Tests;

public sealed class InvalidationLoggingTests
{
    private const string Secret = "tenant:secret-123";

    [Fact]
    public void A_failed_invalidation_logs_neither_its_tags_nor_the_exception()
    {
        var logs = new CapturingLogger();
        var registration = new EntityInvalidation<ShopDb>(new EntityFrameworkCoreOptions(), ThrowingCache.Create(), logs);

        registration.Invalidate([Secret, "products"]);

        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("2 tags").And.Contain(nameof(ShopDb)).And.Contain(typeof(InvalidOperationException).FullName!);
        entry.Message.Should().NotContain(Secret).And.NotContain("password");
        entry.Error.Should().BeNull();
    }

    /// <summary>A cache manager whose invalidation fails with text that must not reach the logs.</summary>
    public class ThrowingCache : DispatchProxy
    {
        public static ICacheManager Create() => Create<ICacheManager, ThrowingCache>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException($"Server=cache;password=hunter2 refused {Secret}");
    }

    private sealed class CapturingLogger : ILogger<EntityInvalidation<ShopDb>>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
