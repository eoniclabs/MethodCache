using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
#if METHODCACHE_PREFIX_HASH
using MethodCache.Core.Runtime.Keys;
#endif

namespace MethodCache.Benchmarks.Microbenchmarks;

// Copy this identical source to the before revision. The feature symbol is derived from the presence of
// the candidate Core implementation; the baseline's prefix lookup hashes its complete span as before.
[MemoryDiagnoser]
public class KeyHashLookupBenchmarks
{
    [Params(4, 80, 256)] public int PrefixCharacters { get; set; }
    private ConcurrentDictionary<string, int> _dictionary = null!;
    private string _text = null!;
    private const string Suffix = ":42";
#if NET9_0_OR_GREATER
    private ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _spans;
#if METHODCACHE_PREFIX_HASH
    private ConcurrentDictionary<string, int>.AlternateLookup<PrehashedKey> _prefixes;
    private MarvinState _prefix;
#endif
#endif

    [GlobalSetup]
    public void Setup()
    {
        var prefix = new string('p', PrefixCharacters);
        _text = prefix + Suffix;
#if METHODCACHE_PREFIX_HASH
        _dictionary = new(KeyComparer.Instance);
#else
        _dictionary = new();
#endif
        _dictionary[_text] = 42;
#if NET9_0_OR_GREATER
        _spans = _dictionary.GetAlternateLookup<ReadOnlySpan<char>>();
#if METHODCACHE_PREFIX_HASH
        _prefixes = _dictionary.GetAlternateLookup<PrehashedKey>();
        _prefix = ProcessKeyHash.Prefix(prefix);
#endif
#endif
    }

    [Benchmark]
    public int ReadyStringLookup() => _dictionary.TryGetValue(_text, out var value) ? value : -1;

    [Benchmark]
    public int FullSpanLookup()
    {
#if NET9_0_OR_GREATER
        return _spans.TryGetValue(_text.AsSpan(), out var value) ? value : -1;
#else
        return _dictionary.TryGetValue(new string(_text.AsSpan()), out var value) ? value : -1;
#endif
    }

    [Benchmark]
    public int PrefixLookup()
    {
#if NET9_0_OR_GREATER && METHODCACHE_PREFIX_HASH
        var hash = _prefix;
        hash.AppendUtf16(Suffix);
        return _prefixes.TryGetValue(new PrehashedKey(_text.AsSpan(), hash.Finish32()), out var value) ? value : -1;
#else
        return FullSpanLookup();
#endif
    }
}
