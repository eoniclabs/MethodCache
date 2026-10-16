using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.PolicyPipeline.Resolution;

/// <summary>
/// Loads the cache policies at host startup (through <c>ValidateOnStart</c>), so invalid startup code or
/// appsettings fail <c>StartAsync</c> and unknown method ids are logged then, instead of on the first cached call.
/// Without a generic host, the first cached call loads them and fails the same way.
/// </summary>
internal sealed class PolicyStartupValidation
{
    internal sealed class Validator(IServiceProvider services) : IValidateOptions<PolicyStartupValidation>
    {
        public ValidateOptionsResult Validate(string? name, PolicyStartupValidation options)
        {
            services.GetService<MethodCacheServiceCollectionExtensions.RegistrationValidation>()?.Validate();
            // Exceptions propagate unchanged, so startup fails with the same exception a first call would throw
            _ = services.GetService<IOptions<MethodCacheMemoryOptions>>()?.Value;
            services.GetRequiredService<PolicyRegistry>().GetAllPolicies();
            return ValidateOptionsResult.Success;
        }
    }
}
