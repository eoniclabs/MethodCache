# Policy Sources

Sources feed cache policies into the policy pipeline. When several sources configure the same method, the one with
the higher priority wins field by field (see `PolicySourcePriority`).

| Source | Priority | Where the policies come from | Live changes |
|--------|----------|------------------------------|--------------|
| Generated attribute policies | `Attributes` | `[Cache]` attributes, emitted by the source generator | No |
| `FluentPolicySource` | `StartupFluent` | `AddMethodCache(config => ...)` | No |
| `ConfigFilePolicySource` | `ConfigurationFiles` | An `IConfiguration` section, usually `MethodCache` in appsettings.json (`AddMethodCacheFromConfiguration`) | Yes, on configuration reload |
| `RuntimePolicySource` | `RuntimeOverrides` | `IRuntimeCacheConfigurator` and the admin endpoints | Yes |
