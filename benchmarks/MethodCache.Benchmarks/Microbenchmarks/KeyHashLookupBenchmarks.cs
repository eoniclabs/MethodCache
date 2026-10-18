using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
#if METHODCACHE_PREFIX_HASH
using MethodCache.Core.Runtime.Keys;
#if METHODCACHE_ASCII_HASH
using PrefixHashState = MethodCache.Core.Runtime.Keys.KeyPrefixHash;
#elif METHODCACHE_SIPHASH
using PrefixHashState = MethodCache.Core.Runtime.Keys.SipHashState;
#else
using PrefixHashState = MethodCache.Core.Runtime.Keys.MarvinState;
#endif
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

    public static bool UsesSipHash
    {
        get
        {
#if METHODCACHE_PREFIX_HASH && METHODCACHE_SIPHASH
            return true;
#else
            return false;
#endif
        }
    }

    public static bool UsesAsciiHash
    {
        get
        {
#if METHODCACHE_PREFIX_HASH && METHODCACHE_ASCII_HASH
            return true;
#else
            return false;
#endif
        }
    }

    [Params(4, 80, 256)] public int PrefixCharacters { get; set; }
    [Params("Int", "Guid")] public string ArgumentKind { get; set; } = "Guid";
    public bool SelectPerformanceCores { get; set; } = true;
    private ConcurrentDictionary<string, int> _dictionary = null!;
    private string _text = null!;
    private string _suffix = null!;
    private ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _spans;
#if METHODCACHE_PREFIX_HASH
    private ConcurrentDictionary<string, int>.AlternateLookup<PrehashedKey> _prefixes;
    private PrefixHashState _prefix;
#endif

    [GlobalSetup]
    public void Setup()
    {
        if (SelectPerformanceCores) PrefixBenchmarkSetup.PreferPerformanceCores();
        var prefix = new string('p', PrefixCharacters);
        _suffix = ArgumentKind switch
        {
            "Int" => ":42",
            "Guid" => ":" + MethodCacheKeys.Part(new Guid("6f9619ff-8b86-d011-b42d-00cf4fc964ff")),
            _ => throw new InvalidOperationException("Unknown benchmark argument kind.")
        };
        _text = prefix + _suffix;
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
#if METHODCACHE_ASCII_HASH
        var hash = _prefix.Finish(_suffix);
#elif METHODCACHE_SIPHASH
        var hash = _prefix.FinishUtf16(_suffix);
#else
        var state = _prefix;
        state.AppendUtf16(_suffix);
        var hash = state.Finish32();
#endif
        return _prefixes.TryGetValue(new PrehashedKey(_text.AsSpan(), hash), out var value) ? value : -1;
#else
        return FullSpanLookup();
#endif
    }
}
