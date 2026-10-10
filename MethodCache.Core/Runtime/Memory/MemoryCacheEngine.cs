using System.Collections.Concurrent;

namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// Eviction algorithm used by <see cref="MemoryCacheEngine"/>.
/// </summary>
internal enum EngineEvictionMode
{
    /// <summary>W-TinyLFU: small LRU admission window, frequency-gated segmented LRU main space.</summary>
    TinyLfu,

    /// <summary>Plain least-recently-used.</summary>
    Lru,

    /// <summary>First-in-first-out (reads do not reorder entries).</summary>
    Fifo
}

/// <summary>
/// Bounded, concurrent in-memory store with per-entry expiration, tags and pluggable eviction.
/// </summary>
/// <remarks>
/// Design follows Caffeine / BitFaster.Caching:
/// <list type="bullet">
/// <item>Reads are lock-free: a dictionary lookup plus a lossy offer into a striped read buffer.</item>
/// <item>Writes update the dictionary immediately and enqueue a policy task.</item>
/// <item>Policy work (reordering, admission, eviction, expiration) is batched in a maintenance step
/// under a single lock that callers only ever try to take, so no caller waits on another.</item>
/// <item>Expiration uses a hierarchical timer wheel, so expired entries are found in O(1) without scanning.</item>
/// </list>
/// </remarks>
internal sealed class MemoryCacheEngine
{
    /// <summary>How long a scheduled drain may wait for the thread pool before readers drain inline.</summary>
    private const long DrainStarvationThresholdNanos = 1_000_000; // 1 ms

    private const long InitialSketchCapacity = 64;

    private enum WriteKind : byte
    {
        Add,
        Remove
    }

    private readonly record struct WriteTask(Node Node, WriteKind Kind);

    private readonly ConcurrentDictionary<string, Node> _map;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Node, byte>> _tagIndex = new(StringComparer.Ordinal);
    private readonly ReadBuffer _readBuffer = new();
    private readonly ConcurrentQueue<WriteTask> _writeBuffer = new();
    private readonly object _evictionLock = new();

    private readonly AccessOrderDeque _window = new();
    private readonly AccessOrderDeque _probation = new();
    private readonly AccessOrderDeque _protected = new();
    private readonly FrequencySketch _sketch;
    private readonly TimerWheel _timerWheel;

    private readonly Func<long> _clock;
    private readonly EngineEvictionMode _mode;
    private readonly long _maximum;
    private readonly long _windowMaximum;
    private readonly long _protectedMaximum;

    private readonly Action<Node> _onAccess;
    private readonly Func<Node, bool> _onTimerExpired;

    // Guarded by _evictionLock; read without the lock only for statistics.
    private long _totalWeight;
    private long _windowWeight;
    private long _protectedWeight;
    private long _evictionCount;
    private long _expirationCount;
    private int _maintenanceScheduled;
    private long _entryCount;
    private long _sketchCapacity = InitialSketchCapacity;
    private long _maintenanceScheduledAt;

    public MemoryCacheEngine(long maximum, EngineEvictionMode mode, Func<long> clock, int expectedItems)
    {
        if (maximum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "Maximum must be greater than zero.");
        }

        _maximum = maximum;
        _mode = mode;
        _clock = clock;
        _map = new ConcurrentDictionary<string, Node>(Environment.ProcessorCount, Math.Min(expectedItems, 1024)); // default comparer enables the non-randomized string hashing fast path

        if (mode == EngineEvictionMode.TinyLfu)
        {
            _windowMaximum = Math.Max(1, (long)(maximum * 0.01));
            var mainMaximum = maximum - _windowMaximum;
            _protectedMaximum = (long)(mainMaximum * 0.8);
        }

        // Sized lazily from the live entry count (see OnAdd), so a huge or weight-based maximum costs nothing up front.
        _sketch = new FrequencySketch(InitialSketchCapacity);
        _timerWheel = new TimerWheel(clock());
        _onAccess = OnAccess;
        _onTimerExpired = OnTimerExpired;
    }

    public long Now => _clock();

    public long Maximum => _maximum;

    /// <summary>
    /// Drains a full read buffer on the reading thread instead of the thread pool, so no access is ever dropped.
    /// Makes policy decisions deterministic for tests; costs read throughput under contention.
    /// </summary>
    public bool ForegroundMaintenance { get; set; }

    /// <summary>Total weight of entries known to the eviction policy.</summary>
    public long TotalWeight => Volatile.Read(ref _totalWeight);

    public long EvictionCount => Volatile.Read(ref _evictionCount);

    public long ExpirationCount => Volatile.Read(ref _expirationCount);

    /// <summary>Number of entries. O(1) but takes the dictionary's internal locks; use for diagnostics only.</summary>
    public int Count => _map.Count;

    public ICollection<string> Tags => _tagIndex.Keys;

    /// <summary>The underlying map, for diagnostics such as memory estimation. Do not mutate.</summary>
    public ConcurrentDictionary<string, Node> Map => _map;

    /// <summary>
    /// Looks up a live (not physically expired) node and records the access.
    /// </summary>
    public bool TryGet(string key, long now, out Node node)
    {
        if (_map.TryGetValue(key, out node!))
        {
            if (!node.IsPhysicallyExpired(now))
            {
                RecordAccess(node, now);
                return true;
            }

            RemoveNode(node);
        }

        node = null!;
        return false;
    }

    /// <summary>Looks up a node without recording an access or checking expiration.</summary>
    public bool TryPeek(string key, out Node node) => _map.TryGetValue(key, out node!);

    private void RecordAccess(Node node, long now)
    {
        if (_readBuffer.Offer(node) == ReadBuffer.OfferResult.Full)
        {
            OnReadBufferFull(now);
        }
    }

    /// <summary>
    /// Inserts or replaces the entry for <see cref="Node.Key"/>. Expiration, weight and tags must already be set.
    /// </summary>
    public void Add(Node node)
    {
        // Tags are indexed before the node becomes visible so a concurrent removal always finds them.
        IndexTags(node);

        Node? replaced = null;
        while (true)
        {
            if (_map.TryAdd(node.Key, node))
            {
                break;
            }

            if (_map.TryGetValue(node.Key, out var existing) && _map.TryUpdate(node.Key, node, existing))
            {
                replaced = existing;
                break;
            }
        }

        if (replaced != null)
        {
            Retire(replaced);
        }

        _writeBuffer.Enqueue(new WriteTask(node, WriteKind.Add));
        AfterWrite();
    }

    public bool Remove(string key)
    {
        if (_map.TryRemove(key, out var node))
        {
            Retire(node);
            AfterWrite();
            return true;
        }

        return false;
    }

    /// <summary>Removes <paramref name="node"/> only if it is still the current entry for its key.</summary>
    public bool RemoveNode(Node node)
    {
        if (_map.TryRemove(new KeyValuePair<string, Node>(node.Key, node)))
        {
            Retire(node);
            AfterWrite();
            return true;
        }

        return false;
    }

    public int RemoveByTag(string tag)
    {
        if (!_tagIndex.TryGetValue(tag, out var nodes))
        {
            return 0;
        }

        var removed = 0;
        foreach (var node in nodes.Keys)
        {
            if (RemoveNode(node))
            {
                removed++;
            }
        }

        return removed;
    }

    public void Clear()
    {
        lock (_evictionLock)
        {
            foreach (var pair in _map)
            {
                if (_map.TryRemove(pair))
                {
                    Interlocked.CompareExchange(ref pair.Value.State, Node.StateRetired, Node.StateAlive);
                    Unlink(pair.Value);
                }
            }

            RunMaintenance();
            _evictionCount = 0;
            _expirationCount = 0;
        }

        // Writers that published while the lock was held left their tasks for the holder to pick up.
        if (!_writeBuffer.IsEmpty)
        {
            AfterWrite();
        }
    }

    /// <summary>Runs pending policy work. Safe to call from any thread; never blocks on another caller.</summary>
    public void Maintain()
    {
        lock (_evictionLock)
        {
            RunMaintenance();
        }

        // A writer whose TryEnter failed while we held the lock relies on the holder to pick up its task.
        if (!_writeBuffer.IsEmpty)
        {
            AfterWrite();
        }
    }

    private void Retire(Node node)
    {
        Interlocked.CompareExchange(ref node.State, Node.StateRetired, Node.StateAlive);
        _writeBuffer.Enqueue(new WriteTask(node, WriteKind.Remove));
    }

    private void AfterWrite()
    {
        // Every lock holder re-checks the queue after releasing, so a failed TryEnter never strands a write.
        do
        {
            if (!Monitor.TryEnter(_evictionLock))
            {
                return;
            }

            try
            {
                RunMaintenance();
            }
            finally
            {
                Monitor.Exit(_evictionLock);
            }
        }
        while (!_writeBuffer.IsEmpty);
    }

    /// <summary>
    /// Drains the read buffer on the thread pool so readers never do policy work themselves.
    /// At most one drain is queued at a time; accesses that arrive while it is pending are dropped.
    /// If the pool has not picked the drain up in time (starvation, a saturated machine), a reader helps out inline,
    /// at most once per <see cref="DrainStarvationThresholdNanos"/>, so the policy keeps receiving frequency information.
    /// Readers never block: a busy lock just drops the access.
    /// </summary>
    private void OnReadBufferFull(long now)
    {
        if (ForegroundMaintenance)
        {
            Maintain();
            return;
        }

        if (Volatile.Read(ref _maintenanceScheduled) == 0)
        {
            ScheduleMaintenance();
            return;
        }

        // 'now' comes from the caller's clock read, so the common dropped-access path costs no extra time query.
        if (now - Volatile.Read(ref _maintenanceScheduledAt) < DrainStarvationThresholdNanos
            || !Monitor.TryEnter(_evictionLock))
        {
            return;
        }

        try
        {
            RunMaintenance();
        }
        finally
        {
            // Restart the clock so readers help at most once per threshold while the pool stays behind.
            Volatile.Write(ref _maintenanceScheduledAt, _clock());
            Monitor.Exit(_evictionLock);
        }

        if (!_writeBuffer.IsEmpty)
        {
            AfterWrite();
        }
    }

    private void ScheduleMaintenance()
    {
        // Callers check the flag with a plain read first, so full-buffer readers do not hammer its cache line.
        if (Interlocked.CompareExchange(ref _maintenanceScheduled, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _maintenanceScheduledAt, _clock());

        ThreadPool.UnsafeQueueUserWorkItem(static engine =>
        {
            try
            {
                engine.Maintain();
            }
            finally
            {
                Volatile.Write(ref engine._maintenanceScheduled, 0);
            }
        }, this, preferLocal: false);
    }

    private void RunMaintenance()
    {
        _readBuffer.Drain(_onAccess);

        while (_writeBuffer.TryDequeue(out var task))
        {
            if (task.Kind == WriteKind.Add)
            {
                OnAdd(task.Node);
            }
            else
            {
                Unlink(task.Node);
            }
        }

        _timerWheel.Advance(_clock(), _onTimerExpired);
        Evict();
    }

    private void OnAdd(Node node)
    {
        if (!node.IsAlive || node.Queue != QueueType.None)
        {
            return;
        }

        if (node.Weight > _maximum)
        {
            // Could never fit: evict it now instead of flushing every other entry first.
            EvictNode(node);
            return;
        }

        _entryCount++;
        if (_mode == EngineEvictionMode.TinyLfu)
        {
            if (_entryCount > _sketchCapacity)
            {
                _sketchCapacity = _entryCount * 2;
                _sketch.EnsureCapacity(_sketchCapacity);
            }

            _sketch.Increment(node.KeyHash);
        }

        node.Queue = QueueType.Window;
        _window.AddLast(node);
        _windowWeight += node.Weight;
        Volatile.Write(ref _totalWeight, _totalWeight + node.Weight);
        _timerWheel.Schedule(node);
    }

    private void OnAccess(Node node)
    {
        if (node.Queue == QueueType.None)
        {
            return;
        }

        switch (_mode)
        {
            case EngineEvictionMode.Fifo:
                return;
            case EngineEvictionMode.Lru:
                _window.MoveToEnd(node);
                return;
        }

        _sketch.Increment(node.KeyHash);

        switch (node.Queue)
        {
            case QueueType.Window:
                _window.MoveToEnd(node);
                break;
            case QueueType.Probation:
                _probation.Remove(node);
                node.Queue = QueueType.Protected;
                _protected.AddLast(node);
                _protectedWeight += node.Weight;
                DemoteFromProtected();
                break;
            case QueueType.Protected:
                _protected.MoveToEnd(node);
                break;
        }
    }

    private void DemoteFromProtected()
    {
        while (_protectedWeight > _protectedMaximum)
        {
            var demoted = _protected.First;
            if (demoted == null)
            {
                break;
            }

            _protected.Remove(demoted);
            _protectedWeight -= demoted.Weight;
            demoted.Queue = QueueType.Probation;
            _probation.AddLast(demoted);
        }
    }

    private bool OnTimerExpired(Node node)
    {
        // Already detached from the wheel by the caller.
        if (_map.TryRemove(new KeyValuePair<string, Node>(node.Key, node)))
        {
            Interlocked.CompareExchange(ref node.State, Node.StateRetired, Node.StateAlive);
            _expirationCount++;
        }

        Unlink(node);
        return true;
    }

    private void Unlink(Node node)
    {
        switch (node.Queue)
        {
            case QueueType.Window:
                _window.Remove(node);
                _windowWeight -= node.Weight;
                break;
            case QueueType.Probation:
                _probation.Remove(node);
                break;
            case QueueType.Protected:
                _protected.Remove(node);
                _protectedWeight -= node.Weight;
                break;
        }

        if (node.Queue != QueueType.None)
        {
            node.Queue = QueueType.None;
            _entryCount--;
            Volatile.Write(ref _totalWeight, _totalWeight - node.Weight);
        }

        _timerWheel.Deschedule(node);

        if (node.State != Node.StateDead)
        {
            UnindexTags(node);
            Volatile.Write(ref node.State, Node.StateDead);
        }
    }

    private void Evict()
    {
        if (_mode != EngineEvictionMode.TinyLfu)
        {
            while (_totalWeight > _maximum && _window.First is { } victim)
            {
                EvictNode(victim);
            }

            return;
        }

        // Move overflow from the admission window into probation; those nodes are the candidates.
        Node? candidate = null;
        while (_windowWeight > _windowMaximum && _window.First is { } moved)
        {
            _window.Remove(moved);
            _windowWeight -= moved.Weight;
            moved.Queue = QueueType.Probation;
            _probation.AddLast(moved);
            candidate ??= moved;
        }

        while (_totalWeight > _maximum)
        {
            var victim = _probation.First;
            if (victim == candidate)
            {
                victim = _protected.First;
            }

            if (victim == null && candidate == null)
            {
                victim = _window.First;
                if (victim == null)
                {
                    break;
                }

                EvictNode(victim);
                continue;
            }

            if (victim == null)
            {
                var next = candidate!.Next;
                EvictNode(candidate);
                candidate = next;
                continue;
            }

            if (candidate == null)
            {
                EvictNode(victim);
                continue;
            }

            if (Admit(candidate, victim))
            {
                EvictNode(victim);
            }
            else
            {
                var next = candidate.Next;
                EvictNode(candidate);
                candidate = next;
            }
        }
    }

    private bool Admit(Node candidate, Node victim)
    {
        var candidateFrequency = _sketch.Frequency(candidate.KeyHash);
        var victimFrequency = _sketch.Frequency(victim.KeyHash);

        if (candidateFrequency > victimFrequency)
        {
            return true;
        }

        // Admit a warm candidate occasionally so an attacker cannot pin the victim with hash flooding.
        return candidateFrequency > 5 && (Random.Shared.Next() & 127) == 0;
    }

    private void EvictNode(Node node)
    {
        if (_map.TryRemove(new KeyValuePair<string, Node>(node.Key, node)))
        {
            Interlocked.CompareExchange(ref node.State, Node.StateRetired, Node.StateAlive);
            _evictionCount++;
        }

        Unlink(node);
    }

    private void IndexTags(Node node)
    {
        var tags = node.Tags;
        if (tags == null)
        {
            return;
        }

        foreach (var tag in tags)
        {
            while (true)
            {
                var nodes = _tagIndex.GetOrAdd(tag, static _ => new ConcurrentDictionary<Node, byte>(ReferenceEqualityComparer.Instance));
                nodes.TryAdd(node, 0);

                // UnindexTags may have dropped this set as empty just before the add; retry on the live set.
                if (_tagIndex.TryGetValue(tag, out var current) && ReferenceEquals(current, nodes))
                {
                    break;
                }
            }
        }
    }

    private void UnindexTags(Node node)
    {
        var tags = node.Tags;
        if (tags == null)
        {
            return;
        }

        foreach (var tag in tags)
        {
            if (!_tagIndex.TryGetValue(tag, out var nodes))
            {
                continue;
            }

            nodes.TryRemove(node, out _);

            if (nodes.IsEmpty && _tagIndex.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Node, byte>>(tag, nodes)) && !nodes.IsEmpty)
            {
                // A concurrent writer added to the set while it was being dropped; put its nodes back.
                var current = _tagIndex.GetOrAdd(tag, nodes);
                if (!ReferenceEquals(current, nodes))
                {
                    foreach (var survivor in nodes.Keys)
                    {
                        current.TryAdd(survivor, 0);
                    }
                }
            }
        }
    }
}

