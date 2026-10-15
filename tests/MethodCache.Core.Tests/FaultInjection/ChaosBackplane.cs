using MethodCache.Extensibility;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>What the hub does with a published message.</summary>
public enum BackplaneChaos
{
    /// <summary>Delivered to every other member at once, before the publish returns.</summary>
    Deliver,

    /// <summary>Lost.</summary>
    Drop,

    /// <summary>Delivered after <see cref="ChaosBackplaneHub.Delay"/> on the hub's <see cref="TimeProvider"/>.</summary>
    Delay,

    /// <summary>Delivered twice.</summary>
    Duplicate,

    /// <summary>Kept until <see cref="ChaosBackplaneHub.DeliverHeld"/>, which can deliver them in another order.</summary>
    Hold,
}

/// <summary>
/// An in-process pub/sub that stands in for Redis pub/sub or the SQL Server table, with faults: messages can be
/// dropped (all, or every Nth), delayed on a <see cref="TimeProvider"/>, duplicated, held and reordered, and a member
/// can be disconnected, so its publishes fail and nothing reaches it.
/// </summary>
internal sealed class ChaosBackplaneHub(TimeProvider? time = null)
{
    private readonly object _lock = new();
    private readonly List<Member> _members = new();
    private readonly List<BackplaneMessage> _held = new();
    private readonly List<ITimer> _timers = new();
    private readonly List<BackplaneMessage> _log = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _published;

    public BackplaneChaos Mode { get; set; } = BackplaneChaos.Deliver;

    /// <summary>With <see cref="BackplaneChaos.Drop"/>, 1 drops every message, 2 every second one, ...</summary>
    public int DropEveryNth { get; set; } = 1;

    public TimeSpan Delay { get; set; }

    /// <summary>Messages published so far, including the ones dropped.</summary>
    public int Published => Volatile.Read(ref _published);

    public int Dropped { get; private set; }

    public int Delivered { get; private set; }

    public int HeldCount
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>Every message published so far, in order, whatever happened to it.</summary>
    public IReadOnlyList<BackplaneMessage> Log
    {
        get
        {
            lock (_lock)
            {
                return _log.ToArray();
            }
        }
    }

    /// <summary>Delivers a message again, as an at-least-once transport may.</summary>
    public Task Redeliver(BackplaneMessage message) => DeliverAsync(message);

    public Member Join()
    {
        var member = new Member(this);
        lock (_lock)
        {
            _members.Add(member);
        }

        return member;
    }

    /// <summary>Delivers the held messages, in publish order or reversed.</summary>
    public async Task DeliverHeld(bool reverse = false)
    {
        BackplaneMessage[] held;
        lock (_lock)
        {
            held = reverse ? Enumerable.Reverse(_held).ToArray() : _held.ToArray();
            _held.Clear();
        }

        foreach (var message in held)
        {
            await DeliverAsync(message);
        }
    }

    private async Task PublishAsync(Member sender, BackplaneMessage message)
    {
        if (sender.Disconnected)
        {
            throw new ChaosException("Backplane disconnected");
        }

        var number = Interlocked.Increment(ref _published);
        lock (_lock)
        {
            _log.Add(message);
        }

        switch (Mode)
        {
            case BackplaneChaos.Drop when number % DropEveryNth == 0:
                lock (_lock)
                {
                    Dropped++;
                }

                return;

            case BackplaneChaos.Delay:
                lock (_lock)
                {
                    _timers.Add(_time.CreateTimer(_ => DeliverAsync(message).GetAwaiter().GetResult(), null, Delay, Timeout.InfiniteTimeSpan));
                }

                return;

            case BackplaneChaos.Duplicate:
                await DeliverAsync(message);
                await DeliverAsync(message);
                return;

            case BackplaneChaos.Hold:
                lock (_lock)
                {
                    _held.Add(message);
                }

                return;

            default:
                await DeliverAsync(message);
                return;
        }
    }

    private async Task DeliverAsync(BackplaneMessage message)
    {
        Member[] members;
        lock (_lock)
        {
            members = _members.ToArray();
            Delivered++;
        }

        // Like Redis pub/sub, the sender gets its own message too; the cache ignores it by instance id
        foreach (var member in members)
        {
            if (!member.Disconnected && member.Handler is { } handler)
            {
                await handler(message);
            }
        }
    }

    public sealed class Member(ChaosBackplaneHub hub) : IBackplane
    {
        public Func<BackplaneMessage, Task>? Handler { get; private set; }

        /// <summary>While set, this member's publishes throw and messages to it are lost.</summary>
        public bool Disconnected { get; set; }

        public string InstanceId { get; } = Guid.NewGuid().ToString("N");

        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) =>
            hub.PublishAsync(this, new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = key, InstanceId = InstanceId });

        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) =>
            hub.PublishAsync(this, new BackplaneMessage { Type = BackplaneMessageType.TagInvalidation, Tag = tag, InstanceId = InstanceId });

        public Task PublishClearAsync(CancellationToken cancellationToken = default) =>
            hub.PublishAsync(this, new BackplaneMessage { Type = BackplaneMessageType.ClearAll, InstanceId = InstanceId });

        public Task PublishMethodInvalidationAsync(string methodId, CancellationToken cancellationToken = default) =>
            hub.PublishAsync(this, new BackplaneMessage { Type = BackplaneMessageType.MethodInvalidation, Key = methodId, InstanceId = InstanceId });

        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
        {
            Handler = onMessage;
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            Handler = null;
            return Task.CompletedTask;
        }
    }
}
