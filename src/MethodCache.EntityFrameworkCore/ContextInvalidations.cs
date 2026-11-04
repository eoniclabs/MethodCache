using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MethodCache.EntityFrameworkCore;

/// <summary>
/// The tags one context instance waits to invalidate. It lives in the context's own services, so a pooled context
/// starts each lease empty.
/// </summary>
internal sealed class ContextInvalidations : IResettableService, IDisposable
{
    private readonly ICurrentDbContext _current;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public ContextInvalidations(ICurrentDbContext current, IDbContextOptions options)
    {
        _current = current;
        Registration = options.FindExtension<CoreOptionsExtension>()?.Interceptors?.OfType<InvalidationInterceptor>().FirstOrDefault()?.Registration;
    }

    /// <summary>The registration, or <see langword="null"/> when the context wasn't set up with <c>UseEntityFrameworkCore</c>.</summary>
    public EntityInvalidation? Registration { get; }

    /// <summary>Tags from the old values of the entries in the running <c>SaveChanges</c>.</summary>
    public HashSet<string> Saving { get; } = new(StringComparer.Ordinal);

    /// <summary>Entries whose new values give tags once the running <c>SaveChanges</c> has succeeded.</summary>
    public List<(EntityEntry Entry, EntityTagTemplate[] Templates)> SavedEntries { get; } = [];

    public void Add(IEnumerable<string> tags)
    {
        foreach (var tag in tags) _pending.Add(tag);
    }

    /// <summary>Moves the tags of a successful <c>SaveChanges</c> to the pending ones.</summary>
    public void SaveSucceeded()
    {
        foreach (var (entry, templates) in SavedEntries) EntityInvalidation.AddTags(templates, entry.CurrentValues, _pending);
        _pending.UnionWith(Saving);
        SaveEnded();
    }

    /// <summary>Forgets the tags of a <c>SaveChanges</c> that failed or was canceled: nothing of it was saved.</summary>
    public void SaveEnded()
    {
        Saving.Clear();
        SavedEntries.Clear();
    }

    /// <summary>Forgets every pending tag: the transaction was rolled back.</summary>
    public void Discard() => _pending.Clear();

    /// <summary>
    /// Invalidates the pending tags unless a transaction is still open: then its commit does. With an ambient
    /// <see cref="Transaction"/>, the tags go with it and are invalidated when it completes without aborting.
    /// </summary>
    public ValueTask SettleAsync(bool async)
    {
        if (_pending.Count == 0 || Registration == null) return ValueTask.CompletedTask;
        if (_current.Context.Database.CurrentTransaction != null) return ValueTask.CompletedTask;
        return FlushAsync(async);
    }

    /// <summary>Invalidates every pending tag, or hands them to the open ambient transaction.</summary>
    public ValueTask FlushAsync(bool async)
    {
        if (_pending.Count == 0 || Registration == null) return ValueTask.CompletedTask;
        var tags = _pending.ToArray();
        _pending.Clear();

        var registration = Registration;
        var ambient = Transaction.Current;
        if (ambient != null && ambient.TransactionInformation.Status == TransactionStatus.Active)
        {
            ambient.TransactionCompleted += (_, completed) =>
            {
                // Committed or in doubt: an extra invalidation is harmless, a missing one serves old data
                if (completed.Transaction?.TransactionInformation.Status != TransactionStatus.Aborted) registration.Invalidate(tags);
            };
            return ValueTask.CompletedTask;
        }

        if (async) return registration.InvalidateAsync(tags);
        registration.Invalidate(tags);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The context is returned to its pool. Tags still pending were queued outside a transaction without a later
    /// <c>SaveChanges</c>, or belong to a transaction that was never committed: invalidating them is late or extra,
    /// both harmless, while dropping them could leave old data cached.
    /// </summary>
    public void ResetState()
    {
        SaveEnded();
        _ = FlushAsync(async: false);
    }

    public Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        ResetState();
        return Task.CompletedTask;
    }

    /// <summary>The context is disposed: the same as returning it to a pool.</summary>
    public void Dispose() => ResetState();
}
