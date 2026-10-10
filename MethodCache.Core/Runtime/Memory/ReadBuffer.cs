using System.Numerics;

namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// Striped, lossy, multi-producer / single-consumer ring buffer that records cache hits.
/// Readers never block: when a stripe is full or contended the access is simply dropped,
/// which only makes the eviction policy slightly less precise. Drained under the eviction lock.
/// </summary>
internal sealed class ReadBuffer
{
    private const int StripeCapacity = 128;
    private const int StripeMask = StripeCapacity - 1;
    private const int Padding = 16; // 16 longs = 128 bytes, keeps counters on separate cache lines

    private readonly Node?[][] _slots;
    private readonly long[] _heads;
    private readonly long[] _tails;
    private readonly int _stripeMask;

    public ReadBuffer()
    {
        var stripes = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(Environment.ProcessorCount * 4, 4, 256));
        _stripeMask = stripes - 1;
        _slots = new Node?[stripes][];
        for (var i = 0; i < stripes; i++)
        {
            _slots[i] = new Node?[StripeCapacity];
        }

        _heads = new long[stripes * Padding];
        _tails = new long[stripes * Padding];
    }

    public enum OfferResult
    {
        Success,
        Failed,
        Full
    }

    public OfferResult Offer(Node node)
    {
        var stripe = Environment.CurrentManagedThreadId & _stripeMask;
        var counterIndex = stripe * Padding;

        var tail = Volatile.Read(ref _tails[counterIndex]);
        var head = Volatile.Read(ref _heads[counterIndex]);
        if (tail - head >= StripeCapacity)
        {
            return OfferResult.Full;
        }

        if (Interlocked.CompareExchange(ref _tails[counterIndex], tail + 1, tail) != tail)
        {
            return OfferResult.Failed;
        }

        Volatile.Write(ref _slots[stripe][(int)(tail & StripeMask)], node);
        return OfferResult.Success;
    }

    /// <summary>Single consumer only.</summary>
    public void Drain(Action<Node> consumer)
    {
        for (var stripe = 0; stripe < _slots.Length; stripe++)
        {
            var counterIndex = stripe * Padding;
            var slots = _slots[stripe];
            var head = Volatile.Read(ref _heads[counterIndex]);
            var tail = Volatile.Read(ref _tails[counterIndex]);

            while (head < tail)
            {
                var index = (int)(head & StripeMask);
                var node = Volatile.Read(ref slots[index]);
                if (node == null)
                {
                    break; // producer claimed the slot but has not published yet
                }

                slots[index] = null;
                head++;
                consumer(node);
            }

            Volatile.Write(ref _heads[counterIndex], head);
        }
    }
}
