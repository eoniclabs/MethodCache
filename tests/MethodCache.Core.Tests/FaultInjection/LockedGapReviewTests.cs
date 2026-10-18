extern alias SqlProvider;

using MethodCache.Extensibility;
using SqlServerBackplane = SqlProvider::MethodCache.SqlServer.SqlServerBackplane;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class LockedGapReviewTests
{
    [Fact]
    public async Task PersistentFailedProbe_PreservesGapAndNewerAndCommittedGapMessages()
    {
        var clock = new FakeTimeProvider();
        var gaps = new List<SqlServerBackplane.GapRange> { new(1, 2, clock.GetUtcNow().UtcDateTime) };
        clock.Advance(TimeSpan.FromMinutes(2));
        var probes = 0;
        var discovered = 0;
        var delivered = new List<string>();
        for (var poll = 0; poll < 3; poll++)
        {
            var ready = new List<BackplaneMessage> { new() { Type = BackplaneMessageType.KeyInvalidation, Key = "new-" + poll } };
            gaps = await SqlServerBackplane.ReadGapSnapshotAsync(gaps, ready, clock.GetUtcNow().UtcDateTime,
                (pending, messages, token) =>
                {
                    Assert.Equal(1, pending[0].From);
                    if (discovered++ == 0)
                    {
                        messages.Add(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = "other-late-gap" });
                        pending = SqlServerBackplane.RemoveTaken(pending, [(2, 2)]);
                    }
                    return Task.FromResult(pending);
                },
                (pending, token) => { probes++; return Task.FromResult(false); }, CancellationToken.None);
            delivered.AddRange(ready.Select(message => message.Key!));
            Assert.Equal(1, Assert.Single(gaps).From);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(new[] { "new-0", "other-late-gap", "new-1", "new-2" }, delivered);
        Assert.Equal(3, probes);
    }

    [Fact]
    public async Task RowCommitsBetweenDiscoveryAndProbe_RemainsWatchedForTheNextPoll()
    {
        var clock = new FakeTimeProvider();
        var gaps = new List<SqlServerBackplane.GapRange> { new(1, 1, clock.GetUtcNow().UtcDateTime) };
        clock.Advance(TimeSpan.FromSeconds(6));
        var ready = new List<BackplaneMessage>();
        gaps = await SqlServerBackplane.ReadGapSnapshotAsync(gaps, ready, clock.GetUtcNow().UtcDateTime,
            (pending, messages, token) => Task.FromResult(pending), (pending, token) => Task.FromResult(false), CancellationToken.None);
        Assert.Single(gaps);
        gaps = await SqlServerBackplane.ReadGapSnapshotAsync(gaps, ready, clock.GetUtcNow().UtcDateTime,
            (pending, messages, token) =>
            {
                messages.Add(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = "late" });
                return Task.FromResult(SqlServerBackplane.RemoveTaken(pending, [(1, 1)]));
            }, (pending, token) => throw new InvalidOperationException("No gap remains to probe"), CancellationToken.None);
        Assert.Empty(gaps);
        Assert.Equal("late", Assert.Single(ready).Key);
    }
}
