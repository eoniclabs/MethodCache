using System.Globalization;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// Shared settings for the CsCheck property tests in this folder.
/// </summary>
/// <remarks>
/// Every property runs from the fixed <see cref="Seed"/>, so a commit always runs the same cases and a failure
/// reproduces on retry. When a property fails, CsCheck shrinks the input and prints the smallest failing case with
/// its seed, for example <c>CsCheck_Seed = "0N0XIzNsQ0O2"</c>. To replay exactly that case, or to explore from
/// another starting point, set that environment variable:
/// <code>
/// CsCheck_Seed=0N0XIzNsQ0O2 dotnet test tests/MethodCache.Core.Tests -f net10.0 --filter "FullyQualifiedName~Properties"
/// </code>
/// Set <c>METHODCACHE_PROPERTY_RANDOM_SEED=true</c> to start from a fresh random seed on every run instead, as a
/// longer exploratory run does. The iteration counts below keep the whole suite at a few seconds per target
/// framework; raise them locally for a longer search (they are passed explicitly, so the <c>CsCheck_Iter</c>
/// environment variable does not change them).
/// </remarks>
internal static class PropertySettings
{
    /// <summary>
    /// The seed every property starts from: <c>CsCheck_Seed</c> when it is set, a fresh random seed when
    /// <c>METHODCACHE_PROPERTY_RANDOM_SEED</c> is <c>true</c>, and a fixed seed otherwise.
    /// </summary>
    public static readonly string? Seed =
        Environment.GetEnvironmentVariable("CsCheck_Seed") is { Length: > 0 } seed ? seed
        : string.Equals(Environment.GetEnvironmentVariable("METHODCACHE_PROPERTY_RANDOM_SEED"), "true", StringComparison.OrdinalIgnoreCase) ? null
        : "0N0XIzNsQ0O2";

    /// <summary>Iterations for cheap, pure properties (keys, tags).</summary>
    public const int PureIterations = 2_000;

    /// <summary>Iterations for model-based runs; each one is a sequence of up to ~100 cache operations.</summary>
    public const int ModelIterations = 1_000;

    /// <summary>Iterations for properties that start real concurrent work.</summary>
    public const int ConcurrentIterations = 200;

    /// <summary>
    /// Cultures CI also runs the unit tests under: tr-TR (dotted/dotless i) and sv-SE (U+2212 minus sign), next to
    /// the invariant culture.
    /// </summary>
    public static readonly CultureInfo[] Cultures =
    {
        CultureInfo.InvariantCulture,
        new("tr-TR"),
        new("sv-SE"),
    };

    /// <summary>Runs <paramref name="action"/> with <paramref name="culture"/> as the current culture of this thread.</summary>
    public static T InCulture<T>(CultureInfo culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
