using MethodCache.Abstractions.Sources;

namespace MethodCache.Core.Configuration.Resolver;

internal sealed record PolicySourceRegistration(IPolicySource Source, int Priority)
{
    public string SourceId => Source.SourceId;
}
