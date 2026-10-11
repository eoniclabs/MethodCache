#nullable enable

namespace MethodCache.Core.PolicyPipeline.Model;

public static class PolicySourcePriority
{
    public const int Attributes = 10;
    public const int StartupFluent = 40;
    public const int ConfigurationFiles = 50;
    public const int RuntimeOverrides = 100;
}
