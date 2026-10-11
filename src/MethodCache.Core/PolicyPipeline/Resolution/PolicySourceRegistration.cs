using MethodCache.Abstractions.Sources;

namespace MethodCache.Core.PolicyPipeline.Resolution;

public sealed record PolicySourceRegistration(IPolicySource Source, int Priority)
{
    public string SourceId => Source.SourceId;
}
