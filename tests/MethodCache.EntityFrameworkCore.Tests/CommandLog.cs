using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MethodCache.EntityFrameworkCore.Tests;

/// <summary>The commands contexts run.</summary>
internal sealed class CommandLog : DbCommandInterceptor
{
    public List<string> Texts { get; } = [];

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Texts.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Texts.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
