using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MethodCache.Infrastructure;

internal sealed class GeneratedDiagnostics(ILogger? logger)
{
    private readonly ConcurrentDictionary<(string Method, string Reason), byte> _reported = new();
    internal void ReportSyncBypass(string method, string reason)
    {
        if (_reported.TryAdd((method, reason), 0))
            logger?.LogWarning("MethodCache synchronous method {Method} uses the local implementation: {Reason}", method, reason);
    }
}
