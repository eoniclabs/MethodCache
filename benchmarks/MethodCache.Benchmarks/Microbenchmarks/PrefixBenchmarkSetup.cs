using System.Runtime.InteropServices;

namespace MethodCache.Benchmarks.Microbenchmarks;

// Shared by the prefix benchmarks and the cost profile, which compiles the generated-interface benchmark alone
internal static class PrefixBenchmarkSetup
{
    internal static void PreferPerformanceCores()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (GetThreadQos(CurrentThread(), out var quality, out _) != 0)
                throw new InvalidOperationException("Could not inspect benchmark thread QoS.");
            // POSIX scheduling opts a thread out of QoS permanently. In-process BDN
            // owns that priority; retain it instead of attempting a forbidden override.
            if (quality != 0 && SetQos(0x19, 0) != 0)
                throw new InvalidOperationException("Could not select benchmark thread QoS.");
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_self")]
    private static extern nint CurrentThread();

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_get_qos_class_np")]
    private static extern int GetThreadQos(nint thread, out uint quality, out int priority);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static extern int SetQos(uint quality, int priority);
}
