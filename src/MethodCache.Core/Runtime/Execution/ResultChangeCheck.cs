using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Extensibility;
using Microsoft.Extensions.Logging;

namespace MethodCache.Core.Runtime.Execution;

/// <summary>
/// In Development only: remembers a hash of what the distributed cache's serializer writes for each value the memory
/// cache keeps and shares, and every few seconds writes the values again. One that writes differently was changed by
/// code after it was cached (see MC0043): the memory cache hands that instance to later callers, while other instances
/// read the stored copy. The method is named in one warning. Nothing of this runs outside Development, and hits never
/// run any of it.
/// </summary>
internal sealed class ResultChangeCheck : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly ISerializer _serializer;
    private readonly ILogger _logger;
    private readonly ConditionalWeakTable<Node, Recorded> _recorded = new();
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);
    private readonly ITimer _timer;
    private int _checking;

    private sealed class Recorded(string method, ulong hash, Writer writer)
    {
        public string Method { get; } = method;
        public ulong Hash { get; } = hash;
        public Writer Writer { get; } = writer;
    }

    internal ResultChangeCheck(ISerializer serializer, ILogger logger, TimeProvider timeProvider)
    {
        _serializer = serializer;
        _logger = logger;
        using (ExecutionContext.SuppressFlow())
        {
            _timer = timeProvider.CreateTimer(static state => ((ResultChangeCheck)state!).Check(), this, Interval, Interval);
        }
    }

    /// <summary>Remembers what the serializer writes for a value the memory cache now keeps.</summary>
    internal void Stored<T>(Node node, string? method, T value)
    {
        if (value is null || method is null) return;
        var writer = Writer<T>.Instance;
        // A serializer that doesn't write the same bytes twice (encryption with a fresh nonce, say) can't be checked
        if (writer.Hash(_serializer, value) is { } hash && writer.Hash(_serializer, value) == hash)
            _recorded.AddOrUpdate(node, new Recorded(method, hash, writer));
    }

    /// <summary>Writes each kept value again, and warns once per method about one that writes differently.</summary>
    internal void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            foreach (var (node, recorded) in _recorded)
            {
                if (!node.IsAlive)
                {
                    _recorded.Remove(node);
                    continue;
                }

                var hash = recorded.Writer.Hash(_serializer, node.BoxedValue);
                // A value being changed right now may fail to write; the next round sees it
                if (hash is null || hash == recorded.Hash) continue;

                _recorded.Remove(node);
                if (_warned.TryAdd(recorded.Method, 0))
                {
                    _logger.LogWarning(
                        "A cached result of {Method} was changed after it was cached: it no longer writes what MethodCache stored. The memory cache hands that same instance to every later caller on this instance, while other instances read the stored copy, so they give different answers. Find the code that changes it (MC0043 names most such code) and change a copy instead, or return a type that can't be changed. Checked in Development only; shown once per method.",
                        recorded.Method);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>Writes a value as its declared type, as the distributed cache does, and hashes what was written.</summary>
    private abstract class Writer
    {
        public abstract ulong? Hash(ISerializer serializer, object? value);
    }

    private sealed class Writer<T> : Writer
    {
        public static readonly Writer<T> Instance = new();

        public override ulong? Hash(ISerializer serializer, object? value)
        {
            if (value is not T typed) return null;
            try
            {
                var buffer = new ArrayBufferWriter<byte>(256);
                serializer.Serialize(typed, buffer);
                return XxHash3.HashToUInt64(buffer.WrittenSpan);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
