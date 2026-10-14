extern alias SqlProvider;

using MethodCache.Extensibility;
using SqlServerBackplane = SqlProvider::MethodCache.SqlServer.SqlServerBackplane;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class LongTransactionReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingTransaction_PastDiscoveryLifetime_IsDeliveredOnCommitOrRetiredOnRollback(bool commit)
    {
        var clock = new FakeTimeProvider();
        var gaps = new List<SqlServerBackplane.GapRange> { new(1, 1, clock.GetUtcNow().UtcDateTime) };
        var messages = new List<BackplaneMessage>();
        var resolved = false;
        Task<List<SqlServerBackplane.GapRange>> Discover(List<SqlServerBackplane.GapRange> pending, List<BackplaneMessage> ready, CancellationToken token)
        {
            if (!resolved || !commit) return Task.FromResult(pending);
            ready.Add(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = "late" });
            return Task.FromResult(new List<SqlServerBackplane.GapRange>());
        }
        Task<bool> Probe(List<SqlServerBackplane.GapRange> pending, CancellationToken token) => Task.FromResult(resolved && !commit);
        clock.Advance(TimeSpan.FromMinutes(2));
        gaps = await SqlServerBackplane.ReadGapSnapshotAsync(gaps, messages, clock.GetUtcNow().UtcDateTime, Discover, Probe, CancellationToken.None);
        Assert.Single(gaps);
        Assert.Empty(messages);

        resolved = true;
        gaps = await SqlServerBackplane.ReadGapSnapshotAsync(gaps, messages, clock.GetUtcNow().UtcDateTime, Discover, Probe, CancellationToken.None);
        Assert.Empty(gaps);
        Assert.Equal(commit ? 1 : 0, messages.Count);
        if (commit) Assert.Equal("late", messages[0].Key);
    }
}
