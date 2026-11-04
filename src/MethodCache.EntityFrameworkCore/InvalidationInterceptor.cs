using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MethodCache.EntityFrameworkCore;

/// <summary>Collects tags while a context saves and invalidates them once the data is committed.</summary>
internal sealed class InvalidationInterceptor(EntityInvalidation registration)
    : ISaveChangesInterceptor, IDbTransactionInterceptor, IDbCommandInterceptor
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
        if (State(eventData.Context) is { } state)
        {
            var saving = state.SavingAsync(async: false, CancellationToken.None);
            // Without async it completes before returning
            if (!saving.IsCompletedSuccessfully) saving.AsTask().GetAwaiter().GetResult();
        }
        return result;
    }

    public async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.SavingAsync(async: true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        _ = State(eventData.Context)?.SavedAsync(async: false);
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.SavedAsync(async: true).ConfigureAwait(false);
        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => _ = State(eventData.Context)?.SaveFailedAsync(async: false);

    public async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.SaveFailedAsync(async: true).ConfigureAwait(false);
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => _ = State(eventData.Context)?.SaveFailedAsync(async: false);

    public async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.SaveFailedAsync(async: true).ConfigureAwait(false);
    }

    // Transactions

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        State(eventData.Context)?.TransactionStarted();
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TransactionStarted(connection, eventData, result));

    public DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData, DbTransaction result)
    {
        State(eventData.Context)?.TransactionUsed(result);
        return result;
    }

    public ValueTask<DbTransaction> TransactionUsedAsync(DbConnection connection, TransactionEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TransactionUsed(connection, eventData, result));

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (State(eventData.Context) is { } state) _ = state.CommittedAsync(transaction, async: false);
        else ExternalTransactions.Resolve(transaction, invalidate: true);
    }

    public async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.CommittedAsync(transaction, async: true).ConfigureAwait(false);
        else ExternalTransactions.Resolve(transaction, invalidate: true);
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (State(eventData.Context) is { } state) state.RolledBack(transaction);
        else ExternalTransactions.Resolve(transaction, invalidate: false);
    }

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        TransactionRolledBack(transaction, eventData);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        => _ = State(eventData.Context)?.FailedAsync(transaction, eventData.Action, async: false);

    public async Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (State(eventData.Context) is { } state) await state.FailedAsync(transaction, eventData.Action, async: true).ConfigureAwait(false);
    }

    // Commands: a query is where the context starts watching which rows it loaded, and a write that completes
    // outside a transaction during a failed save means part of it may be stored

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        State(eventData.Context)?.EnsureStarted();
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ReaderExecuting(command, eventData, result));

    public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        State(eventData.Context)?.CommandCompleted();
        return result;
    }

    public ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ReaderExecuted(command, eventData, result));

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        State(eventData.Context)?.CommandCompleted();
        return result;
    }

    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(NonQueryExecuted(command, eventData, result));

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        State(eventData.Context)?.CommandCompleted();
        return result;
    }

    public ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScalarExecuted(command, eventData, result));
}
