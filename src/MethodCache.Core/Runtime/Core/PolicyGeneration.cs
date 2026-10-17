namespace MethodCache.Infrastructure;

internal sealed class PolicyGeneration
{
    private int _retired;
    internal volatile bool DistributedReady = true;
    internal object Gate { get; } = new();
    internal bool IsCurrent => Volatile.Read(ref _retired) == 0;
    internal void Retire() => Volatile.Write(ref _retired, 1);
}
