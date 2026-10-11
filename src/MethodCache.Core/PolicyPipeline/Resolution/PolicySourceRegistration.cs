namespace MethodCache.Policies;

public sealed record PolicySourceRegistration(IPolicySource Source, int Priority)
{
    public string SourceId => Source.SourceId;
}
