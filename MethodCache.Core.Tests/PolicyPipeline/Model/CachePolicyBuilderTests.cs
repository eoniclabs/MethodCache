using System;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.KeyGenerators;
using Xunit;

namespace MethodCache.Core.Tests.PolicyPipeline.Model;

public class CachePolicyBuilderTests
{
    [Fact]
    public void Build_WithFields_SetsPolicyAndFlags()
    {
        var builder = new CachePolicyBuilder()
            .WithDuration(TimeSpan.FromMinutes(5))
            .AddTag("users")
            .AddTag("profiles")
            .WithKeyGenerator(typeof(FastHashKeyGenerator))
            .WithVersion(2)
            .RequireIdempotent();

        var draft = builder.Build("MethodCache.Tests.UserService.GetAsync", "startup");

        Assert.Equal("MethodCache.Tests.UserService.GetAsync", draft.MethodId);
        Assert.Equal("startup", draft.Notes);
        Assert.Equal(TimeSpan.FromMinutes(5), draft.Policy.Duration);
        Assert.Contains("users", draft.Policy.Tags);
        Assert.Equal(typeof(FastHashKeyGenerator), draft.Policy.KeyGeneratorType);
        Assert.Equal(2, draft.Policy.Version);
        Assert.True(draft.Policy.RequireIdempotent);

        var expectedFields = CachePolicyFields.Duration |
                             CachePolicyFields.Tags |
                             CachePolicyFields.KeyGenerator |
                             CachePolicyFields.Version |
                             CachePolicyFields.RequireIdempotent;

        Assert.Equal(expectedFields, draft.Fields);
    }

    [Fact]
    public void Build_WithMetadata_CopiesEntries()
    {
        var builder = new CachePolicyBuilder()
            .AddMetadata("environment", "prod")
            .AddMetadata("etag.strategy", "strong");

        var draft = builder.Build("MethodCache.Tests.Policy.WithMetadata");

        Assert.Equal(CachePolicyFields.Metadata, draft.Fields);
        Assert.Equal("prod", draft.Policy.Metadata["environment"]);
        Assert.Equal("strong", draft.Policy.Metadata["etag.strategy"]);
        Assert.Same(draft.Policy.Metadata, draft.Metadata);
    }

    [Fact]
    public void Apply_Defaults_PreservesSpecificOverrides()
    {
        var defaults = new CachePolicyBuilder()
            .WithDuration(TimeSpan.FromMinutes(10))
            .AddTag("default")
            .RequireIdempotent();

        var specific = new CachePolicyBuilder()
            .WithVersion(3)
            .AddTag("specific");

        specific.Apply(defaults, overwriteExisting: false);
        var draft = specific.Build("MethodCache.Tests.Policy.WithDefaults");

        Assert.Equal(TimeSpan.FromMinutes(10), draft.Policy.Duration);
        Assert.Contains("specific", draft.Policy.Tags);
        Assert.DoesNotContain("default", draft.Policy.Tags);
        Assert.Equal(3, draft.Policy.Version);
        Assert.True(draft.Policy.RequireIdempotent);
    }
}
