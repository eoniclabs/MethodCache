using System.Numerics;

namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// Hierarchical timer wheel giving O(1) scheduling and expiration of entries with individual lifetimes.
/// Port of the design used by Caffeine (Varghese &amp; Lauck, "Hashed and Hierarchical Timing Wheels").
/// Times are engine nanoseconds. Not thread-safe; guarded by the engine's eviction lock.
/// </summary>
internal sealed class TimerWheel
{
    private static readonly int[] Buckets = { 64, 64, 32, 4, 1 };

    private static readonly long[] Spans =
    {
        (long)BitOperations.RoundUpToPowerOf2((ulong)TimeSpan.FromSeconds(1).Ticks * 100),  // 1.07s
        (long)BitOperations.RoundUpToPowerOf2((ulong)TimeSpan.FromMinutes(1).Ticks * 100),  // 1.14m
        (long)BitOperations.RoundUpToPowerOf2((ulong)TimeSpan.FromHours(1).Ticks * 100),    // 1.22h
        (long)BitOperations.RoundUpToPowerOf2((ulong)TimeSpan.FromDays(1).Ticks * 100),     // 1.63d
        4 * (long)BitOperations.RoundUpToPowerOf2((ulong)TimeSpan.FromDays(1).Ticks * 100), // 6.5d
        4 * (long)BitOperations.RoundUpToPowerOf2((ulong)TimeSpan.FromDays(1).Ticks * 100)  // 6.5d
    };

    private static readonly int[] Shifts =
    {
        BitOperations.TrailingZeroCount(Spans[0]),
        BitOperations.TrailingZeroCount(Spans[1]),
        BitOperations.TrailingZeroCount(Spans[2]),
        BitOperations.TrailingZeroCount(Spans[3]),
        BitOperations.TrailingZeroCount(Spans[4])
    };

    private readonly SentinelNode[][] _wheel;
    private long _nanos;

    public TimerWheel(long now)
    {
        _nanos = now;
        _wheel = new SentinelNode[Buckets.Length][];
        for (var i = 0; i < _wheel.Length; i++)
        {
            _wheel[i] = new SentinelNode[Buckets[i]];
            for (var j = 0; j < _wheel[i].Length; j++)
            {
                _wheel[i][j] = new SentinelNode();
            }
        }
    }

    /// <summary>
    /// Advances the wheel to <paramref name="now"/>, handing every node whose bucket has elapsed to
    /// <paramref name="onExpired"/>. The callback returns false when the node is still live (for example
    /// a sliding expiration was extended) and must be rescheduled.
    /// </summary>
    public void Advance(long now, Func<Node, bool> onExpired)
    {
        var previous = _nanos;
        _nanos = now;

        for (var i = 0; i < Shifts.Length; i++)
        {
            var previousTicks = previous >> Shifts[i];
            var currentTicks = now >> Shifts[i];
            var delta = currentTicks - previousTicks;
            if (delta <= 0)
            {
                break;
            }

            Expire(i, previousTicks, delta, onExpired);
        }
    }

    public void Schedule(Node node)
    {
        if (node.TimerNext != null)
        {
            Deschedule(node);
        }

        var sentinel = FindBucket(Volatile.Read(ref node.PhysicalExpiresAt));
        Link(sentinel, node);
    }

    public void Deschedule(Node node)
    {
        var next = node.TimerNext;
        if (next == null)
        {
            return;
        }

        var previous = node.TimerPrevious!;
        next.TimerPrevious = previous;
        previous.TimerNext = next;
        node.TimerNext = null;
        node.TimerPrevious = null;
    }

    private void Expire(int index, long previousTicks, long delta, Func<Node, bool> onExpired)
    {
        var timerWheel = _wheel[index];
        var mask = timerWheel.Length - 1;
        var steps = (int)Math.Min(1 + delta, timerWheel.Length);
        var start = (int)(previousTicks & mask);
        var end = start + steps;

        for (var i = start; i < end; i++)
        {
            var sentinel = timerWheel[i & mask];
            var node = sentinel.TimerNext!;
            sentinel.TimerPrevious = sentinel;
            sentinel.TimerNext = sentinel;

            while (node != sentinel)
            {
                var next = node.TimerNext!;
                node.TimerPrevious = null;
                node.TimerNext = null;

                if (node.IsAlive && (Volatile.Read(ref node.PhysicalExpiresAt) - _nanos > 0 || !onExpired(node)))
                {
                    Schedule(node);
                }

                node = next;
            }
        }
    }

    private SentinelNode FindBucket(long time)
    {
        var duration = time - _nanos;
        var length = _wheel.Length - 1;

        for (var i = 0; i < length; i++)
        {
            if (duration < Spans[i + 1])
            {
                var ticks = time >> Shifts[i];
                var index = (int)(ticks & (_wheel[i].Length - 1));
                return _wheel[i][index];
            }
        }

        return _wheel[length][0];
    }

    private static void Link(SentinelNode sentinel, Node node)
    {
        node.TimerPrevious = sentinel.TimerPrevious;
        node.TimerNext = sentinel;
        sentinel.TimerPrevious!.TimerNext = node;
        sentinel.TimerPrevious = node;
    }
}
