using System.Net.Http.Headers;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Builds Warning headers according to RFC 9111 Section 5.5.
/// Warning headers inform caches and clients about potential issues with cached responses.
/// </summary>
public class WarningHeaderBuilder
{
    /// <summary>
    /// Warning codes defined in RFC 9111.
    /// </summary>
    public enum WarningCode
    {
        /// <summary>
        /// 110 - Response is Stale
        /// The response is stale (its age is greater than its freshness lifetime).
        /// </summary>
        ResponseIsStale = 110,

        /// <summary>
        /// 111 - Revalidation Failed
        /// The proxy was unable to validate the response and is returning it stale.
        /// </summary>
        RevalidationFailed = 111,

        /// <summary>
        /// 112 - Disconnected Operation
        /// The cache is disconnected from the network.
        /// </summary>
        DisconnectedOperation = 112,

        /// <summary>
        /// 113 - Heuristic Expiration
        /// The cache used heuristic freshness to determine expiration.
        /// </summary>
        HeuristicExpiration = 113,

        /// <summary>
        /// 199 - Miscellaneous Warning
        /// Arbitrary warning text that should be presented to the user.
        /// </summary>
        MiscellaneousWarning = 199,

        /// <summary>
        /// 214 - Transformation Applied
        /// The proxy has applied a transformation to the response.
        /// </summary>
        TransformationApplied = 214,

        /// <summary>
        /// 299 - Miscellaneous Persistent Warning
        /// Arbitrary persistent warning text.
        /// </summary>
        MiscellaneousPersistentWarning = 299
    }

    private readonly List<WarningHeaderValue> _warnings = new();
    private readonly string _hostName;

    public WarningHeaderBuilder(string? hostName = null)
    {
        _hostName = hostName ?? Environment.MachineName;
    }

    /// <summary>
    /// Adds a warning header.
    /// </summary>
    /// <param name="code">The warning code.</param>
    /// <param name="text">The warning text.</param>
    /// <param name="date">Optional date when the warning was generated.</param>
    /// <returns>The builder for chaining.</returns>
    public WarningHeaderBuilder AddWarning(WarningCode code, string text, DateTimeOffset? date = null)
    {
        var agent = $"{_hostName}:{Environment.ProcessId}";
        var warning = date.HasValue
            ? new WarningHeaderValue((int)code, agent, $"\"{text}\"", date.Value)
            : new WarningHeaderValue((int)code, agent, $"\"{text}\"");

        _warnings.Add(warning);
        return this;
    }

    /// <summary>
    /// Adds a warning for a stale response.
    /// </summary>
    /// <param name="reason">Optional reason why the response is stale.</param>
    /// <returns>The builder for chaining.</returns>
    public WarningHeaderBuilder AddStaleWarning(string? reason = null)
    {
        var text = reason ?? "Response is stale";
        return AddWarning(WarningCode.ResponseIsStale, text);
    }

    /// <summary>
    /// Adds a warning for failed revalidation.
    /// </summary>
    /// <param name="reason">Optional reason for revalidation failure.</param>
    /// <returns>The builder for chaining.</returns>
    public WarningHeaderBuilder AddRevalidationFailedWarning(string? reason = null)
    {
        var text = reason ?? "Revalidation failed";
        return AddWarning(WarningCode.RevalidationFailed, text);
    }

    /// <summary>
    /// Adds a warning for heuristic expiration.
    /// </summary>
    /// <param name="heuristicAge">The heuristic age used.</param>
    /// <returns>The builder for chaining.</returns>
    public WarningHeaderBuilder AddHeuristicExpirationWarning(TimeSpan heuristicAge)
    {
        var hours = (int)heuristicAge.TotalHours;
        var text = $"Heuristic expiration used ({hours} hours)";
        return AddWarning(WarningCode.HeuristicExpiration, text);
    }

    /// <summary>
    /// Adds a warning for disconnected operation.
    /// </summary>
    /// <returns>The builder for chaining.</returns>
    public WarningHeaderBuilder AddDisconnectedWarning()
    {
        return AddWarning(WarningCode.DisconnectedOperation, "Cache is disconnected");
    }

    /// <summary>
    /// Gets all warning headers.
    /// </summary>
    /// <returns>The collection of warning headers.</returns>
    public IEnumerable<WarningHeaderValue> GetWarnings()
    {
        return _warnings;
    }

    /// <summary>
    /// Applies warnings to an HTTP response message.
    /// </summary>
    /// <param name="response">The response to apply warnings to.</param>
    public void ApplyTo(HttpResponseMessage response)
    {
        if (!_warnings.Any())
            return;

        foreach (var warning in _warnings)
        {
            response.Headers.Warning.Add(warning);
        }
    }

    /// <summary>
    /// Removes 1xx warning codes from a response (as required when serving from cache).
    /// RFC 9111: "A cache MUST NOT generate a new Warning header field when
    /// forwarding a response and MUST NOT add a Warning header field to a
    /// response that does not have one."
    /// </summary>
    /// <param name="response">The response to clean.</param>
    public static void Remove1xxWarnings(HttpResponseMessage response)
    {
        if (!response.Headers.Warning.Any())
            return;

        var non1xxWarnings = response.Headers.Warning
            .Where(w => w.Code < 100 || w.Code >= 200)
            .ToList();

        response.Headers.Warning.Clear();
        foreach (var warning in non1xxWarnings)
        {
            response.Headers.Warning.Add(warning);
        }
    }

    /// <summary>
    /// Checks if a response has any warning headers.
    /// </summary>
    /// <param name="response">The response to check.</param>
    /// <returns>True if the response has warning headers.</returns>
    public static bool HasWarnings(HttpResponseMessage response)
    {
        return response.Headers.Warning.Any();
    }

    /// <summary>
    /// Checks if a response has a specific warning code.
    /// </summary>
    /// <param name="response">The response to check.</param>
    /// <param name="code">The warning code to check for.</param>
    /// <returns>True if the response has the specified warning code.</returns>
    public static bool HasWarningCode(HttpResponseMessage response, WarningCode code)
    {
        return response.Headers.Warning.Any(w => w.Code == (int)code);
    }
}