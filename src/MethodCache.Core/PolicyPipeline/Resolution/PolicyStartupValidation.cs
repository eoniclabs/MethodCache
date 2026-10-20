using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.PolicyPipeline.Resolution;

/// <summary>
/// Loads the cache policies at host startup (through <c>ValidateOnStart</c>), so invalid startup code or
/// appsettings, a policy for a method that isn't cached and a tag template that doesn't fit its method fail
/// <c>StartAsync</c> instead of the first cached call.
/// Without a generic host, the first cached call loads them and fails the same way.
/// </summary>
internal sealed class PolicyStartupValidation
{
    internal sealed class Validator(IServiceProvider services) : IValidateOptions<PolicyStartupValidation>
    {
        public ValidateOptionsResult Validate(string? name, PolicyStartupValidation options)
        {
            services.GetService<MethodCacheServiceCollectionExtensions.RegistrationValidationState>()?.Validate();
            // Exceptions propagate unchanged, so startup fails with the same exception a first call would throw
            services.GetService<IOptions<MethodCacheMemoryOptions>>()?.Value.EnsureValid();
            services.GetRequiredService<PolicyRegistry>().GetAllPolicies();
            return ValidateOptionsResult.Success;
        }
    }
}
