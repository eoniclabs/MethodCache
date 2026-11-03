using Npgsql;

namespace MethodCache.PostgreSql;

/// <summary>
/// The connection pool MethodCache owns: one <see cref="NpgsqlDataSource"/> for cache commands, and the settings of
/// the separate, unpooled connection each instance keeps open for <c>LISTEN</c>.
/// </summary>
internal sealed class PostgreSqlConnection : IAsyncDisposable, IDisposable
{
    // Enough slots for every fixed command; each pooled connection keeps them prepared across uses
    internal const int DefaultMaxAutoPrepare = 32;
    internal const string DefaultApplicationName = "MethodCache";
    internal static readonly TimeSpan ListenKeepAlive = TimeSpan.FromSeconds(30);

    internal PostgreSqlConnection(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // Prepared statements skip parsing and planning on every call. An explicit Max Auto Prepare (0 behind
        // PgBouncer versions without prepared statement support) is kept.
        if (!Names(connectionString, "maxautoprepare")) builder.MaxAutoPrepare = DefaultMaxAutoPrepare;
        // MethodCache's commands change no session state (settings are transaction-local), so a connection back in
        // the pool needs no reset: that saves the server a reset batch before every command
        if (!Names(connectionString, "noresetonclose")) builder.NoResetOnClose = true;
        if (string.IsNullOrEmpty(builder.ApplicationName)) builder.ApplicationName = DefaultApplicationName;
        // Cache commands never join the application's TransactionScope, even with Enlist=true: a scope that rolls back
        // must not undo cache writes or invalidations, and creating the tables runs a transaction of its own
        builder.Enlist = false;
        CommandConnectionString = builder.ConnectionString;
        DataSource = NpgsqlDataSource.Create(CommandConnectionString);

        // LISTEN holds one connection for as long as the instance runs, so it doesn't take a slot in the pool. The
        // keepalive notices a connection that died silently, so the instance reconnects instead of waiting forever.
        var listen = new NpgsqlConnectionStringBuilder(CommandConnectionString) { Pooling = false, MaxAutoPrepare = 0 };
        if (listen.KeepAlive == 0) listen.KeepAlive = (int)ListenKeepAlive.TotalSeconds;
        ListenConnectionString = listen.ConnectionString;
    }

    /// <summary>Whether <paramref name="connectionString"/> sets <paramref name="keyword"/>, written with any case or spaces.</summary>
    private static bool Names(string connectionString, string keyword)
    {
        // The typed builder reports every keyword it knows as present, so the raw pairs are read instead
        var raw = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in raw.Keys)
        {
            if (string.Equals(key.Replace(" ", "", StringComparison.Ordinal), keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>The pool every cache command uses.</summary>
    internal NpgsqlDataSource DataSource { get; }

    internal string CommandConnectionString { get; }

    internal string ListenConnectionString { get; }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();

    public void Dispose() => DataSource.Dispose();
}
