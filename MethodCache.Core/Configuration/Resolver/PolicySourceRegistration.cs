using MethodCache.Abstractions.Sources;

namespace MethodCache.Core.Configuration.Resolver;

public sealed record PolicySourceRegistration(IPolicySource Source, int Priority)
{
    public string SourceId => Source.SourceId;
}
