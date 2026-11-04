using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace MethodCache.EntityFrameworkCore;

/// <summary>
/// Tags saved inside a transaction passed in with <c>UseTransaction</c>. Its owner commits it, which a context only
/// sees when the owner is a context connected to MethodCache. Otherwise the tags wait until the transaction is
/// known to be over: its connection closes, or it has no connection any more when the context is disposed.
/// Invalidating any earlier could let a reader cache the old rows again before the commit.
/// </summary>
internal static class ExternalTransactions
{
    private static readonly ConditionalWeakTable<DbTransaction, Pending> Table = new();

    public static void Add(DbTransaction transaction, EntityInvalidation registration, IEnumerable<string> tags)
    {
        var pending = Table.GetValue(transaction, static t => new Pending(t));
        if (!pending.TryAdd(registration, tags)) registration.Invalidate(tags.ToArray());
    }

    /// <summary>A context connected to MethodCache committed or rolled back <paramref name="transaction"/>.</summary>
    public static void Resolve(DbTransaction transaction, bool invalidate)
    {
        if (Table.TryGetValue(transaction, out var pending)) pending.Resolve(invalidate);
    }

    /// <summary>A context that saved inside <paramref name="transaction"/> is disposed or returned to its pool.</summary>
    public static void Released(DbTransaction transaction)
    {
        if (Table.TryGetValue(transaction, out var pending) && IsOver(transaction)) pending.Resolve(invalidate: true);
    }

    /// <summary>A committed or rolled back transaction has no connection.</summary>
    private static bool IsOver(DbTransaction transaction)
    {
        try
        {
            return transaction.Connection == null;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private sealed class Pending
    {
        private readonly DbTransaction _transaction;
        private readonly DbConnection? _connection;
        private readonly Dictionary<EntityInvalidation, HashSet<string>> _tags = new();
        private bool _resolved;

        public Pending(DbTransaction transaction)
        {
            _transaction = transaction;
            _connection = transaction.Connection;
            // A transaction ends at the latest when its connection closes, committed or rolled back
            if (_connection != null) _connection.StateChange += OnStateChange;
        }

        public bool TryAdd(EntityInvalidation registration, IEnumerable<string> tags)
        {
            lock (_tags)
            {
                if (_resolved) return false;
                if (!_tags.TryGetValue(registration, out var set)) _tags[registration] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(tags);
                return true;
            }
        }

        public void Resolve(bool invalidate)
        {
            KeyValuePair<EntityInvalidation, HashSet<string>>[] tags;
            lock (_tags)
            {
                if (_resolved) return;
                _resolved = true;
                tags = _tags.ToArray();
                _tags.Clear();
            }
            if (_connection != null) _connection.StateChange -= OnStateChange;
            Table.Remove(_transaction);
            if (!invalidate) return;
            foreach (var (registration, set) in tags) registration.Invalidate(set.ToArray());
        }

        private void OnStateChange(object? sender, StateChangeEventArgs e)
        {
            if (e.CurrentState is ConnectionState.Closed or ConnectionState.Broken) Resolve(invalidate: true);
        }
    }
}
