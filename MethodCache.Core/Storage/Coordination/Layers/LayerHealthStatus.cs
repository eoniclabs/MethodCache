using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.Core.Storage.Coordination.Layers;

/// <summary>
/// Health status for a storage layer.
/// </summary>
public sealed record LayerHealthStatus(
    string LayerId,
    HealthStatus Status,
    string? Message = null,
    Dictionary<string, object>? Details = null);
