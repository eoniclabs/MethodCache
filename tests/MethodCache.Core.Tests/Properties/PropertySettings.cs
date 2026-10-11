using System.Globalization;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// Shared settings for the CsCheck property tests in this folder.
/// </summary>
/// <remarks>
/// Every property starts from a random seed, so each run explores new cases. When a property fails, CsCheck shrinks
/// the input and prints the smallest failing case with its seed, for example
/// <c>CsCheck_Seed = "0N0XIzNsQ0O2"</c>. To replay exactly that case, run the test with that environment variable set:
/// <code>
/// CsCheck_Seed=0N0XIzNsQ0O2 dotnet test tests/MethodCache.Core.Tests -f net10.0 --filter "FullyQualifiedName~Properties"
/// </code>
/// (or pass <c>seed: "0N0XIzNsQ0O2"</c> to the failing <c>Sample</c> call). The iteration counts below keep the whole
/// suite at a few seconds per target framework; raise them locally for a longer search (they are passed explicitly,
/// so the <c>CsCheck_Iter</c> environment variable does not change them).
/// </remarks>
internal static class PropertySettings
{
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
