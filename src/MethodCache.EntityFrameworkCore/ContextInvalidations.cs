using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace MethodCache.EntityFrameworkCore;

/// <summary>
/// What one context instance waits to invalidate, and what it knows about the rows it tracks. It lives in the
/// context's own services, so a pooled context starts each lease empty.
/// </summary>
/// <remarks>
/// Tags are never invalidated before their data is committed: a reader in between would cache the old rows again.
/// Where the outcome is unknown they are invalidated, since an extra invalidation only costs a cache miss.
/// </remarks>
internal sealed class ContextInvalidations : IResettableService, IDisposable
{
    private readonly ICurrentDbContext _current;

    // Queued outside a transaction: invalidated at the next commit, a successful SaveChanges without a transaction
    // or the commit of a transaction. A rollback keeps them, since they don't belong to it.
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);

    // Saved or queued inside _transaction: invalidated when it commits, dropped when it rolls back
    private readonly HashSet<string> _inTransaction = new(StringComparer.Ordinal);
    private IDbContextTransaction? _transaction;

    // Passed in with UseTransaction: its owner commits it, so its tags wait in ExternalTransactions
    private DbTransaction? _external;
    private readonly HashSet<DbTransaction> _externalUsed = new(ReferenceEqualityComparer.Instance);

    // Rows whose original values didn't come from the database: attached, updated or added rather than queried
    private readonly HashSet<object> _unloaded = new(ReferenceEqualityComparer.Instance);

    private ChangeTracker? _tracker;
    private EntityTagMap? _map;
    private bool _relational;
    private bool _trackedSubscribed;
    private bool _stateChangingSubscribed;
    private SaveRun? _save;

    public ContextInvalidations(ICurrentDbContext current, IDbContextOptions options)
    {
        _current = current;
        Registration = options.FindExtension<CoreOptionsExtension>()?.Interceptors?.OfType<InvalidationInterceptor>().FirstOrDefault()?.Registration;
    }

    /// <summary>The registration, or <see langword="null"/> when the context wasn't set up with <c>UseEntityFrameworkCore</c>.</summary>
    public EntityInvalidation? Registration { get; }

    /// <summary>
    /// Starts watching the context's rows, at its first query, save or queued tag. Rows it tracks already count as
    /// attached, since nothing says they came from the database.
    /// </summary>
    public void EnsureStarted()
    {
        if (_tracker != null || Registration == null) return;
        var context = _current.Context;
        _tracker = context.ChangeTracker;
        _relational = context.Database.IsRelational();
        // A tag that doesn't fit the model fails the next save, with the reason
        if (!Registration.TryGetMap(context.Model, out var map) || map.IsEmpty) return;
        _map = map;
        if (map.NeedsStoredValues)
        {
            foreach (var entry in Snapshot()) MarkUnloaded(entry);
        }
        UpdateSubscriptions();
    }

    // InvalidateAfterCommit

    public void Add(IEnumerable<string> tags)
    {
        // A running save without a transaction is the commit these wait for
        if (_save is { InTransaction: false })
        {
            _waiting.UnionWith(tags);
            return;
        }
        SyncTransaction();
        if (_current.Context.Database.CurrentTransaction == null) _waiting.UnionWith(tags);
        else AddToTransaction(new HashSet<string>(tags, StringComparer.Ordinal));
    }

    // SaveChanges

    /// <summary>
    /// A save starts: records the rows it writes and the old values of their tags. Rows a later interceptor or the
    /// save itself changes, such as a cascade delete, are recorded as they change.
    /// </summary>
    public ValueTask SavingAsync(bool async, CancellationToken cancellationToken)
    {
        EnsureStarted();
        var context = _current.Context;
        SyncTransaction();
        _map ??= Registration!.GetMap(context.Model);
        var save = _save = new SaveRun(context.Database.CurrentTransaction != null);
        if (_map.IsEmpty) return default;
        UpdateSubscriptions();

        var rows = Snapshot();
        save.Collecting = true;
        try
        {
            // A row attached without loading may have changes the save hasn't detected yet. Detecting them for these
            // rows alone keeps the save's own detection the only full one.
            if (_tracker!.AutoDetectChangesEnabled && _unloaded.Count > 0)
            {
                foreach (var entry in rows)
                {
                    if (entry.State is EntityState.Unchanged or EntityState.Modified && _unloaded.Contains(entry.Entity)) entry.DetectChanges();
                }
            }
            foreach (var entry in rows)
            {
                var state = entry.State;
                if (state is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    Capture(entry, takeOld: state != EntityState.Added, wasAdded: state == EntityState.Added);
                }
            }
        }
        finally
        {
            save.Collecting = false;
        }

        List<SavedRow>? toRead = null;
        foreach (var row in save.Rows)
        {
            if (row.NeedsRead) (toRead ??= []).Add(row);
        }
        return toRead == null ? default : ReadStoredAsync(save, toRead, async, cancellationToken);
    }

    /// <summary>The save succeeded: its tags are invalidated now, or with the transaction it ran in.</summary>
    public ValueTask SavedAsync(bool async)
    {
        if (EndSave() is not { } save) return default;
        var tags = save.Tags;
        AddNewTags(save, tags, succeeded: true);
        if (save.InTransaction)
        {
            AddToTransaction(tags);
            return default;
        }
        tags.UnionWith(_waiting);
        _waiting.Clear();
        return FlushAsync(tags, async);
    }

    /// <summary>The save threw or was canceled.</summary>
    public ValueTask SaveFailedAsync(bool async)
    {
        if (EndSave() is not { } save) return default;
        var tags = save.Tags;
        AddNewTags(save, tags, succeeded: false);
        // Inside a transaction, whatever part of the save reached it commits or rolls back with it
        if (save.InTransaction)
        {
            AddToTransaction(tags);
            return default;
        }
        // Without one, nothing was written when EF Core's transaction rolled back or no write completed outside it.
        // Otherwise part of the save may be in the database.
        var written = !_relational || save.CommitUncertain || save.AutoCommitted || save.CommandCompletedOutsideTransaction;
        return written ? FlushAsync(tags, async) : default;
    }

    /// <summary>A command finished on the context's connection.</summary>
    public void CommandCompleted()
    {
        if (_save is { InTransaction: false, AutoTransaction: false, Reading: false } save) save.CommandCompletedOutsideTransaction = true;
    }

    // Transactions

    public void TransactionStarted()
    {
        // Inside a save without a transaction, this is the save's own transaction, which the save itself settles
        if (_save is { InTransaction: false } save)
        {
            save.AutoTransaction = true;
            return;
        }
        SyncTransaction();
    }

    public void TransactionUsed(DbTransaction transaction)
    {
        SyncTransaction();
        _external = transaction;
    }

    public ValueTask CommittedAsync(DbTransaction transaction, bool async)
    {
        ExternalTransactions.Resolve(transaction, invalidate: true);
        if (_save is { InTransaction: false } save)
        {
            save.AutoTransaction = false;
            save.AutoCommitted = true;
            return default;
        }
        if (ReferenceEquals(transaction, _external)) _external = null;
        _transaction = null;
        return FlushAsync(Take(_inTransaction, _waiting), async);
    }

    public void RolledBack(DbTransaction transaction)
    {
        ExternalTransactions.Resolve(transaction, invalidate: false);
        if (_save is { InTransaction: false } save)
        {
            save.AutoTransaction = false;
            return;
        }
        if (ReferenceEquals(transaction, _external)) _external = null;
        _transaction = null;
        _inTransaction.Clear();
    }

    /// <summary>
    /// A commit that fails leaves the outcome unknown, so its tags are invalidated. A failed rollback committed
    /// nothing. A failed savepoint leaves the transaction open.
    /// </summary>
    public ValueTask FailedAsync(DbTransaction transaction, string action, bool async)
    {
        if (action == "Rollback")
        {
            RolledBack(transaction);
            return default;
        }
        if (action != "Commit") return default;

        ExternalTransactions.Resolve(transaction, invalidate: true);
        if (_save is { InTransaction: false } save)
        {
            save.AutoTransaction = false;
            save.CommitUncertain = true;
            return default;
        }
        if (ReferenceEquals(transaction, _external)) _external = null;
        _transaction = null;
        return FlushAsync(Take(_inTransaction, _waiting), async);
    }

    /// <summary>
    /// Notices a transaction that ended without EF Core saying how: committed through its <see cref="DbTransaction"/>,
    /// or disposed without a commit. Its tags are invalidated, since an extra invalidation is harmless and a missing
    /// one keeps old data.
    /// </summary>
    private void SyncTransaction()
    {
        var current = _current.Context.Database.CurrentTransaction;
        if (_transaction != null && !ReferenceEquals(current, _transaction))
        {
            _transaction = null;
            _ = FlushAsync(Take(_inTransaction), async: false);
        }
        if (_external != null && !IsExternal(current)) _external = null;
    }

    private bool IsExternal(IDbContextTransaction? transaction)
        => _external != null && transaction is IInfrastructure<DbTransaction> infrastructure && ReferenceEquals(infrastructure.Instance, _external);

    private void AddToTransaction(HashSet<string> tags)
    {
        if (tags.Count == 0) return;
        SyncTransaction();
        var current = _current.Context.Database.CurrentTransaction;
        if (current == null)
        {
            _ = FlushAsync(tags, async: false);
            return;
        }
        if (IsExternal(current))
        {
            ExternalTransactions.Add(_external!, Registration!, tags);
            _externalUsed.Add(_external!);
            return;
        }
        _transaction = current;
        _inTransaction.UnionWith(tags);
    }

    // Rows

    private void OnTracked(object? sender, EntityTrackedEventArgs e)
    {
        if (e.FromQuery) return;
        var entry = e.Entry;
        if (_map!.NeedsStoredValues) MarkUnloaded(entry);
        if (_save != null && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
        {
            Capture(entry, takeOld: entry.State != EntityState.Added, wasAdded: entry.State == EntityState.Added);
        }
    }

    private void OnStateChanging(object? sender, EntityStateChangingEventArgs e)
    {
        if (_save == null || e.NewState is EntityState.Unchanged or EntityState.Detached) return;
        Capture(e.Entry, takeOld: e.OldState is EntityState.Unchanged or EntityState.Modified or EntityState.Deleted,
            wasAdded: e.NewState == EntityState.Added);
    }

    /// <summary>
    /// Remembers a row attached rather than queried. An owned or join row is remembered too, so a change to it is
    /// found before the save and its owner's stored row can be read.
    /// </summary>
    private void MarkUnloaded(EntityEntry entry)
    {
        if (_map!.TryGet(entry.Metadata, out var tags) && (tags.NeedsStoredValues || tags.Principals.Length > 0)) _unloaded.Add(entry.Entity);
    }

    /// <summary>
    /// Records a row of the running save: the tags of its old values now, its new values after the save. An owned
    /// row or a many-to-many join row records its owner or both sides of the join too.
    /// </summary>
    private void Capture(EntityEntry entry, bool takeOld, bool wasAdded)
    {
        var save = _save!;
        if (!_map!.TryGet(entry.Metadata, out var tags) || save.Seen.ContainsKey(entry.Entity)) return;
        var row = new SavedRow(entry, tags, wasAdded);
        save.Seen.Add(entry.Entity, row);
        save.Rows.Add(row);
        if (takeOld)
        {
            // Attached without loading: its original values may not be the stored row, which is read before the save
            if (save.Collecting && tags.NeedsStoredValues && _unloaded.Contains(entry.Entity)) row.NeedsRead = true;
            else AddTags(tags.Templates, new Values(entry.OriginalValues), save.Tags);
        }
        foreach (var foreignKey in tags.Principals) CapturePrincipals(entry, foreignKey, takeOld);
    }

    private void CapturePrincipals(EntityEntry entry, IForeignKey foreignKey, bool takeOld)
    {
        var oldKey = takeOld ? KeyOf(entry.OriginalValues, foreignKey) : null;
        var newKey = entry.State == EntityState.Deleted ? null : KeyOf(entry.CurrentValues, foreignKey);
        if (oldKey != null) CapturePrincipal(foreignKey, oldKey.Value);
        if (newKey != null && !newKey.Equals(oldKey)) CapturePrincipal(foreignKey, newKey.Value);
    }

    private void CapturePrincipal(IForeignKey foreignKey, KeyValues key)
    {
        if (FindPrincipal(foreignKey.PrincipalKey, key) is { } principal)
        {
            Capture(principal, takeOld: principal.State != EntityState.Added, wasAdded: principal.State == EntityState.Added);
            return;
        }
        // The principal isn't tracked: its tags that use only its key come from the foreign key's values
        if (!_map!.TryGet(foreignKey.PrincipalEntityType, out var tags)) return;
        var keyProperties = foreignKey.PrincipalKey.Properties;
        foreach (var template in tags.Templates)
        {
            if (template.Properties.All(keyProperties.Contains)) _save!.Tags.Add(template.Format(new KeyRow(keyProperties, key)));
        }
    }

    private EntityEntry? FindPrincipal(IKey key, KeyValues values)
    {
        var save = _save!;
        save.Principals ??= new();
        if (!save.Principals.TryGetValue(key, out var index))
        {
            index = new();
            foreach (var candidate in Snapshot())
            {
                if (!key.DeclaringEntityType.IsAssignableFrom(candidate.Metadata)) continue;
                var current = candidate.CurrentValues;
                index.TryAdd(new KeyValues(key.Properties.Select(p => current[p]).ToArray()), candidate);
            }
            save.Principals[key] = index;
        }
        return index.GetValueOrDefault(values);
    }

    private static KeyValues? KeyOf(PropertyValues values, IForeignKey foreignKey)
    {
        var key = new object?[foreignKey.Properties.Count];
        for (var i = 0; i < key.Length; i++)
        {
            if ((key[i] = values[foreignKey.Properties[i]]) == null) return null;
        }
        return new KeyValues(key);
    }

    /// <summary>Reads the stored rows of attached rows the save writes, for the old values of their tags.</summary>
    private static async ValueTask ReadStoredAsync(SaveRun save, List<SavedRow> rows, bool async, CancellationToken cancellationToken)
    {
        save.Reading = true;
        try
        {
            foreach (var row in rows)
            {
                var entry = row.Entry;
                var stored = async
                    ? await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false)
                    : entry.GetDatabaseValues();
                row.Modified = entry.Properties.Where(static p => p.IsModified).Select(static p => p.Metadata).ToHashSet();
                row.Stored = stored;
                AddTags(row.Tags.Templates, new Values(stored ?? entry.OriginalValues), save.Tags);
            }
        }
        finally
        {
            save.Reading = false;
        }
    }

    /// <summary>The tags of the rows' new values: saved values, with generated keys, for rows that still exist.</summary>
    private void AddNewTags(SaveRun save, HashSet<string> tags, bool succeeded)
    {
        foreach (var row in save.Rows)
        {
            var entry = row.Entry;
            if (entry.State is EntityState.Detached or EntityState.Deleted) continue;
            if (row.Stored != null) AddTags(row.Tags.Templates, new WrittenValues(row), tags);
            else AddTags(row.Tags.Templates, new Values(entry.CurrentValues), tags);
            // A new row's values are now the stored ones
            if (succeeded && row.WasAdded) _unloaded.Remove(entry.Entity);
        }
    }

    private static void AddTags<TValues>(EntityTagTemplate[] templates, TValues values, HashSet<string> tags) where TValues : IRowValues
    {
        foreach (var template in templates) tags.Add(template.Format(values));
    }

    private SaveRun? EndSave()
    {
        var save = _save;
        _save = null;
        UpdateSubscriptions();
        return save;
    }

    /// <summary>
    /// Every row the context tracks, without detecting changes: the save detects them once, and the rows they change
    /// are recorded as they change.
    /// </summary>
    private List<EntityEntry> Snapshot()
    {
        var tracker = _tracker!;
        var detect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            return tracker.Entries().ToList();
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = detect;
        }
    }

    /// <summary>
    /// Watches rows being attached when some tag reads more than the key, and every row change while a save runs.
    /// </summary>
    private void UpdateSubscriptions()
    {
        if (_tracker == null || _map == null) return;
        var tracked = _map.NeedsStoredValues || _save != null;
        if (tracked != _trackedSubscribed)
        {
            if (tracked) _tracker.Tracked += OnTracked;
            else _tracker.Tracked -= OnTracked;
            _trackedSubscribed = tracked;
        }
        var changing = _save != null;
        if (changing != _stateChangingSubscribed)
        {
            if (changing) _tracker.StateChanging += OnStateChanging;
            else _tracker.StateChanging -= OnStateChanging;
            _stateChangingSubscribed = changing;
        }
    }

    // Invalidating

    private static HashSet<string> Take(params HashSet<string>[] sets)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            tags.UnionWith(set);
            set.Clear();
        }
        return tags;
    }

    /// <summary>Invalidates <paramref name="tags"/>, or when an open ambient transaction ends.</summary>
    private ValueTask FlushAsync(HashSet<string> tags, bool async)
    {
        if (tags.Count == 0 || Registration == null) return default;
        var array = tags.ToArray();
        var registration = Registration;
        var ambient = Transaction.Current;
        if (ambient is { TransactionInformation.Status: TransactionStatus.Active })
        {
            // Also when it aborts: a connection that isn't enlisted in it has committed already
            ambient.TransactionCompleted += (_, _) => registration.Invalidate(array);
            return default;
        }
        if (async) return registration.InvalidateAsync(array);
        registration.Invalidate(array);
        return default;
    }

    /// <summary>
    /// The context is returned to its pool or disposed. Tags still waiting had no commit after them, or belong to a
    /// transaction that ended without EF Core saying how: invalidating them is late or extra, both harmless, while
    /// dropping them could leave old data cached.
    /// </summary>
    private ValueTask ReleaseAsync(bool async, bool disposing)
    {
        _save = null;
        if (!disposing && _tracker != null)
        {
            if (_trackedSubscribed) _tracker.Tracked -= OnTracked;
            if (_stateChangingSubscribed) _tracker.StateChanging -= OnStateChanging;
        }
        _trackedSubscribed = _stateChangingSubscribed = false;
        _tracker = null;
        _map = null;
        _unloaded.Clear();
        foreach (var transaction in _externalUsed) ExternalTransactions.Released(transaction);
        _externalUsed.Clear();
        _external = null;
        _transaction = null;
        return FlushAsync(Take(_waiting, _inTransaction), async);
    }

    public void ResetState() => _ = ReleaseAsync(async: false, disposing: false);

    public Task ResetStateAsync(CancellationToken cancellationToken = default)
        => ReleaseAsync(async: true, disposing: false).AsTask();

    public void Dispose() => _ = ReleaseAsync(async: false, disposing: true);

    /// <summary>One running <c>SaveChanges</c>.</summary>
    private sealed class SaveRun(bool inTransaction)
    {
        /// <summary>A transaction was open when the save started: its commit settles the save's tags.</summary>
        public bool InTransaction { get; } = inTransaction;

        /// <summary>EF Core's own transaction for the save is open.</summary>
        public bool AutoTransaction { get; set; }

        public bool AutoCommitted { get; set; }

        public bool CommitUncertain { get; set; }

        public bool CommandCompletedOutsideTransaction { get; set; }

        /// <summary>The save reads stored rows of attached rows; those commands write nothing.</summary>
        public bool Reading { get; set; }

        public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);

        public List<SavedRow> Rows { get; } = [];

        /// <summary>The save's own collection runs: rows attached without loading can still be read.</summary>
        public bool Collecting { get; set; }

        public Dictionary<object, SavedRow> Seen { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<IKey, Dictionary<KeyValues, EntityEntry>>? Principals { get; set; }
    }

    private sealed class SavedRow(EntityEntry entry, EntityTags tags, bool wasAdded)
    {
        public EntityEntry Entry { get; } = entry;

        public EntityTags Tags { get; } = tags;

        public bool WasAdded { get; } = wasAdded;

        public bool NeedsRead { get; set; }

        /// <summary>The stored row of a row attached without loading, read before the save.</summary>
        public PropertyValues? Stored { get; set; }

        public HashSet<IProperty>? Modified { get; set; }
    }

    private readonly struct Values(PropertyValues values) : IRowValues
    {
        public object? this[IProperty property] => values[property];
    }

    /// <summary>A row attached without loading, after the save: what it wrote, and the stored values of the rest.</summary>
    private readonly struct WrittenValues(SavedRow row) : IRowValues
    {
        public object? this[IProperty property]
            => row.Modified!.Contains(property) || row.Entry.Property(property).IsModified
                ? row.Entry.CurrentValues[property]
                : row.Stored![property];
    }

    private readonly struct KeyRow(IReadOnlyList<IProperty> properties, KeyValues key) : IRowValues
    {
        public object? this[IProperty property]
        {
            get
            {
                for (var i = 0; i < properties.Count; i++)
                {
                    if (properties[i] == property) return key[i];
                }
                return null;
            }
        }
    }

    private readonly struct KeyValues : IEquatable<KeyValues>
    {
        private readonly object?[] _values;

        public KeyValues(object?[] values) => _values = values;

        public object? this[int index] => _values[index];

        public bool Equals(KeyValues other) => _values.AsSpan().SequenceEqual(other._values);

        public override bool Equals(object? obj) => obj is KeyValues other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in _values) hash.Add(value);
            return hash.ToHashCode();
        }
    }
}
