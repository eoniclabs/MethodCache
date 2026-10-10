using System.Numerics;

namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// Contention-free counter for hot statistics (similar to Java's LongAdder): each thread
/// increments a cell on its own cache line and reads sum all cells.
/// </summary>
internal sealed class StripedCounter
{
    private const int Padding = 16; // 128 bytes between cells

    private readonly long[] _cells;
    private readonly int _mask;

    public StripedCounter()
    {
        var stripes = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(Environment.ProcessorCount, 1, 64));
        _mask = stripes - 1;
        _cells = new long[stripes * Padding];
    }

    public void Increment() => Interlocked.Increment(ref _cells[(Environment.CurrentManagedThreadId & _mask) * Padding]);

    public void Add(long value) => Interlocked.Add(ref _cells[(Environment.CurrentManagedThreadId & _mask) * Padding], value);

    public long Sum()
    {
        long sum = 0;
        for (var i = 0; i < _cells.Length; i += Padding)
        {
            sum += Volatile.Read(ref _cells[i]);
        }

        return sum;
    }

    public void Reset()
    {
        for (var i = 0; i < _cells.Length; i += Padding)
        {
            Volatile.Write(ref _cells[i], 0);
        }
    }
}
