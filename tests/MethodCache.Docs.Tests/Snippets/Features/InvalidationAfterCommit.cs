using System.Data.Common;
using MethodCache;

namespace Docs.InvalidationAfterCommit
{
    public interface IProductReads
    {
        const string ProductTag = "product:{id}";

        [Cache(Duration = "00:10:00", Tags = [ProductTag])]
        Task<string?> GetNameAsync(int id);
    }

    public static class ProductEndpoints
    {
        // begin-snippet: invalidation-after-commit
        public static async Task RenameAsync(int id, string name, DbConnection connection, ICacheManager cache,
            IMethodCacheKeys keys, CancellationToken ct)
        {
            await using (var transaction = await connection.BeginTransactionAsync(ct))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "update products set name = @name where id = @id";
                AddParameter(command, "@name", name);
                AddParameter(command, "@id", id);
                await command.ExecuteNonQueryAsync(ct);
                await transaction.CommitAsync(ct);
            }

            // After the commit, and not cancelled with the request: the data has changed either way
            await cache.InvalidateTagAsync(
                keys.Tag<IProductReads>(r => r.GetNameAsync(id), IProductReads.ProductTag), CancellationToken.None);
        }
        // end-snippet

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
