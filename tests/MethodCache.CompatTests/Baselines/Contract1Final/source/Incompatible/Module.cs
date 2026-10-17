using System.Runtime.CompilerServices;
using MethodCache.Generated;

namespace FrozenVendor.Contract99Final;

public sealed class IncompatibleModule : GeneratedModule
{
    public static int DescribeCalls;

    // Deliberate library initializer: this fixture exercises generated bootstrap loading.
#pragma warning disable CA2255
    [ModuleInitializer]
    public static void Register() => GeneratedCode.Register(99, new IncompatibleModule());

    public override void Describe(GeneratedModuleBuilder builder)
    {
        DescribeCalls++;
        throw new InvalidOperationException("An incompatible descriptor must never execute.");
    }
}
