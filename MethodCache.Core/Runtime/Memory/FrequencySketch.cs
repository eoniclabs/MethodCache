using System.Numerics;

namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// A 4-bit Count-Min sketch that estimates how often a key has been seen recently.
/// Counters are periodically halved ("aging") so the estimate tracks recent popularity.
/// Algorithm follows Caffeine's TinyLFU frequency sketch (Einziger, Friedman, Manes).
/// Not thread-safe; guarded by the engine's eviction lock.
/// </summary>
internal sealed class FrequencySketch
{
    private static readonly ulong[] Seeds =
    {
        0xc3a5c85c97cb3127UL, 0xb492b66fbe98f273UL, 0x9ae16a3b2f90404fUL, 0xcbf29ce484222325UL
    };

    private const ulong ResetMask = 0x7777777777777777UL;
    private const ulong OneMask = 0x1111111111111111UL;

    private ulong[] _table = Array.Empty<ulong>();
    private int _tableMask;
    private int _sampleSize;
    private int _size;

    public FrequencySketch(long maximumSize)
    {
        EnsureCapacity(maximumSize);
    }

    public int SampleSize => _sampleSize;

    public void EnsureCapacity(long maximumSize)
    {
        var maximum = (int)Math.Min(Math.Max(maximumSize, 8), int.MaxValue >> 1);
        if (_table.Length >= maximum)
        {
            return;
        }

        _table = new ulong[BitOperations.RoundUpToPowerOf2((uint)maximum)];
        _tableMask = Math.Max(0, _table.Length - 1);
        _sampleSize = 10 * maximum <= 0 ? int.MaxValue : 10 * maximum;
        _size = 0;
    }

    public int Frequency(int hashCode)
    {
        var hash = Spread(hashCode);
        var start = (hash & 3) << 2;
        var frequency = int.MaxValue;

        for (var i = 0; i < 4; i++)
        {
            var index = IndexOf(hash, i);
            var count = (int)((_table[index] >> ((start + i) << 2)) & 0xfUL);
            frequency = Math.Min(frequency, count);
        }

        return frequency;
    }

    public void Increment(int hashCode)
    {
        var hash = Spread(hashCode);
        var start = (hash & 3) << 2;

        var added = IncrementAt(IndexOf(hash, 0), start);
        added |= IncrementAt(IndexOf(hash, 1), start + 1);
        added |= IncrementAt(IndexOf(hash, 2), start + 2);
        added |= IncrementAt(IndexOf(hash, 3), start + 3);

        if (added && ++_size == _sampleSize)
        {
            Reset();
        }
    }

    private bool IncrementAt(int index, int counter)
    {
        var offset = counter << 2;
        var mask = 0xfUL << offset;

        if ((_table[index] & mask) != mask)
        {
            _table[index] += 1UL << offset;
            return true;
        }

        return false;
    }

    private void Reset()
    {
        var count = 0;
        for (var i = 0; i < _table.Length; i++)
        {
            count += BitOperations.PopCount(_table[i] & OneMask);
            _table[i] = (_table[i] >> 1) & ResetMask;
        }

        _size = (_size >> 1) - (count >> 2);
    }

    private int IndexOf(int item, int i)
    {
        var hash = ((ulong)(uint)item + Seeds[i]) * Seeds[i];
        hash += hash >> 32;
        return (int)hash & _tableMask;
    }

    private static int Spread(int x)
    {
        var y = (uint)x;
        y = ((y >> 16) ^ y) * 0x45d9f3bU;
        y = ((y >> 16) ^ y) * 0x45d9f3bU;
        return (int)((y >> 16) ^ y);
    }
}
