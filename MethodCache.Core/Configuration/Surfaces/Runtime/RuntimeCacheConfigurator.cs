using System;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Options;

namespace MethodCache.Core.Configuration.Runtime;

internal sealed class RuntimeCacheConfigurator : IRuntimeCacheConfigurator
{
    private readonly RuntimePolicyStore _store;

    public RuntimeCacheConfigurator(RuntimePolicyStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new CacheEntryOptions.Builder();
        configure(builder);
        var options = builder.Build();
        var policyBuilder = CachePolicyBuilderFactory.FromOptions(options);
        if (options.Predicate != null)
        {
            policyBuilder.RequireIdempotent();
        }

        var draft = policyBuilder.Build(methodId);
        return _store.UpsertAsync(methodId, draft.Policy, draft.Fields);
    }

    public Task UpsertAsync(string methodId, Action<CachePolicyBuilder> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new CachePolicyBuilder();
        configure(builder);
        var draft = builder.Build(methodId);
        return _store.UpsertAsync(methodId, draft.Policy, draft.Fields);
    }

    public Task UpsertAsync(string methodId, CachePolicy policy, CachePolicyFields fields = CachePolicyFields.None)
        => _store.UpsertAsync(methodId, policy, fields);

    public Task ApplyAsync(Action<IFluentMethodCacheConfiguration> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var fluent = new FluentMethodCacheConfiguration();
        configure(fluent);
        var drafts = fluent.BuildMethodPolicies();

        foreach (var draft in drafts)
        {
            _store.UpsertAsync(draft.MethodId, draft.Policy, draft.Fields);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string methodId) => _store.RemoveAsync(methodId);

    public Task ClearAsync() => _store.ClearAsync();
}
