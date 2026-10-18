using MethodCache.Extensibility;
using MethodCache.Infrastructure;

namespace MethodCache.SqlServer.IntegrationTests;

internal static class SqlWireFixture
{
    // Base64 ASCII contains no SQL quote; raw SQL fault fixtures can embed this deterministic frame safely.
    internal static string Key(string key, string sender = "other") => BackplaneWire.Encode(new BackplaneMessage
    { Type = BackplaneMessageType.KeyInvalidation, Key = key, InstanceId = sender, Timestamp = DateTimeOffset.UnixEpoch });
}
