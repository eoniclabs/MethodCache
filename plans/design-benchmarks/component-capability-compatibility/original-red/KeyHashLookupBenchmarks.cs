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
    // Setup guards check the compiled path against the referenced Core assembly.
    public static bool UsesPrefixHash
    {
        get
        {
#if METHODCACHE_PREFIX_HASH
            return true;
#else
            return false;
#endif
        }
    }

    [Params(4, 80, 256)] public int PrefixCharacters { get; set; }
    public bool SelectPerformanceCores { get; set; } = true;
    private ConcurrentDictionary<string, int> _dictionary = null!;
    private string _text = null!;
    private const string Suffix = ":42";
    private ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _spans;
#if METHODCACHE_PREFIX_HASH
    private ConcurrentDictionary<string, int>.AlternateLookup<PrehashedKey> _prefixes;
    private SipHashState _prefix;
#endif

    [GlobalSetup]
    public void Setup()
    {
        if (SelectPerformanceCores) PrefixBenchmarkSetup.PreferPerformanceCores();
        var prefix = new string('p', PrefixCharacters);
        _text = prefix + Suffix;
#if METHODCACHE_PREFIX_HASH
        _dictionary = new(KeyComparer.Instance);
#else
        _dictionary = new();
#endif
        _dictionary[_text] = 42;
        _spans = _dictionary.GetAlternateLookup<ReadOnlySpan<char>>();
#if METHODCACHE_PREFIX_HASH
        _prefixes = _dictionary.GetAlternateLookup<PrehashedKey>();
        _prefix = ProcessKeyHash.Prefix(prefix);
#endif
    }

    [Benchmark]
    public int ReadyStringLookup() => _dictionary.TryGetValue(_text, out var value) ? value : -1;

    [Benchmark]
    public int FullSpanLookup()
    {
        return _spans.TryGetValue(_text.AsSpan(), out var value) ? value : -1;
    }

    [Benchmark]
    public int PrefixLookup()
    {
#if METHODCACHE_PREFIX_HASH
        return _prefixes.TryGetValue(new PrehashedKey(_text.AsSpan(), _prefix.FinishUtf16(Suffix)), out var value) ? value : -1;
#else
        return FullSpanLookup();
#endif
    }
}
