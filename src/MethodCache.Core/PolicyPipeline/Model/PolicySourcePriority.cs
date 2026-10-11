#nullable enable

namespace MethodCache.Policies;

/// <summary>
/// The priorities of the built-in policy sources; where two sources set the same field, the higher priority wins.
/// </summary>
public static class PolicySourcePriority
{
    /// <summary>
    /// <c>[Cache]</c> attributes in code (10), the lowest priority.
    /// </summary>
    public const int Attributes = 10;
    /// <summary>
    /// Policies configured in startup code (40).
    /// </summary>
    public const int StartupFluent = 40;
    /// <summary>
    /// Policies from configuration files such as appsettings.json (50).
    /// </summary>
    public const int ConfigurationFiles = 50;
    /// <summary>
    /// Overrides applied while the app runs (100), the highest priority.
    /// </summary>
    public const int RuntimeOverrides = 100;
}
