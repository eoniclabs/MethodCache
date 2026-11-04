using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MethodCache.EntityFrameworkCore;

/// <summary>Collects tags while a context saves and invalidates them once the data is committed.</summary>
internal sealed class InvalidationInterceptor(EntityInvalidation registration) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    public EntityInvalidation Registration { get; } = registration;

    internal static ContextInvalidations? State(DbContext? context)
    {
        if (context == null) return null;
        try
        {
            return context.GetService<ContextInvalidations>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // SaveChanges

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (State(eventData.Context) is { } state)
        {
            state.SaveSucceeded();
            _ = state.SettleAsync(async: false);
        }
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state)
        {
            state.SaveSucceeded();
            await state.SettleAsync(async: true).ConfigureAwait(false);
        }
        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => State(eventData.Context)?.SaveEnded();

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => State(eventData.Context)?.SaveEnded();

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesCanceled(eventData);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context == null || State(context) is not { } state) return;
        state.SaveEnded();
        Registration.Collect(context, state);
    }

    // Transactions. EF's own transaction inside SaveChanges also ends here; its tags are still in the running save.

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => _ = State(eventData.Context)?.FlushAsync(async: false);

    public async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.FlushAsync(async: true).ConfigureAwait(false);
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => State(eventData.Context)?.Discard();

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        TransactionRolledBack(transaction, eventData);
        return Task.CompletedTask;
    }

    // A failed commit or rollback leaves the outcome unknown: invalidate, since an extra invalidation is harmless
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        => _ = State(eventData.Context)?.FlushAsync(async: false);

    public async Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.FlushAsync(async: true).ConfigureAwait(false);
    }
}
