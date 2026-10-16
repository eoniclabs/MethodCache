# Generated decorators

Put cache attributes on interface methods. The generator builds a decorator around the registered implementation
and routes cached calls through MethodCache. Your implementation stays ordinary application code.

The generator supports synchronous result methods, `Task<T>` and `ValueTask<T>`, overloaded methods, generic
interfaces and generic methods, including interfaces nested in generic containing types. Constraints on both
the containing type and the interface, including `unmanaged` and base class constraints, are preserved, and so are
`scoped` parameters on forwarded members. Each overload has its own
[method id](configuration.md#appsettingsjson) and its own cache entries.

Public interface members are forwarded to the implementation: uncached methods, properties, indexers and events.
Non-public and sealed default interface methods stay on the interface with their own bodies; the decorator doesn't
implement them, so `[Cache]` or `[CacheInvalidate]` on one (or on a `static` member) is warning
[MC0018](../user-guide/DIAGNOSTICS.md#mc0018). An interface with non-public abstract members is rejected with
[MC0014](../user-guide/DIAGNOSTICS.md#mc0014), because the decorator can't call those members on the implementation.

Keywords and unusual names are escaped, and your own types named like framework types (such as `Task` or `List`)
don't clash with the generated code. You don't need to rename anything.
Long namespace and type names also work with `EmitCompilerGeneratedFiles`: the generator gives those files
short, stable names while preserving the declared types and cache identities.

Unsupported cached method shapes receive a [generator diagnostic](../user-guide/DIAGNOSTICS.md), with a code fix
to remove the cache attribute where available. Reference returns, ref struct results and pointer/function pointer
results are rejected before cached code is emitted. Those methods are forwarded when another method on the
interface is cacheable. See [keys](keys.md) for argument identity and
[configuration](configuration.md) for generated policy identifiers.

## Registration

Generated runtime calls use the versioned `MethodCache.Generated` facade. Module initializers record descriptors
only; `AddMethodCache` validates contract versions before executing descriptors or factories. Each invocation
captures one immutable policy/prefix/publication generation, including keyed registration and background-scope
binding. A retired invocation may finish its origin call but cannot publish into the current generation.

`AddMethodCache()` wraps every registration of a cached interface in its decorator, and keeps the container's
ownership as it is: the container disposes the implementations it creates, and leaves instances you registered
yourself open. Factory registrations are wrapped the same way ([Getting started](../getting-started.md#registering-with-a-factory)).

A factory registration for the README's `IUserService`, once `IUserDatabase` is registered:

<!-- snippet: explicit-factory-registration -->
```cs
builder.Services.AddScoped<IUserService>(sp =>
    new UserService(sp.GetRequiredService<IUserDatabase>()));
builder.Services.AddMethodCache();
```
<!-- endSnippet -->

The end-to-end signature corpus under `tests/MethodCache.SourceGenerator.IntegrationTests` checks supported shapes
with C# 12 and the latest compiler: each generated decorator compiles and runs through the actual DI registration.
It checks semantic runtime references as well. Frozen vendor binaries exercise contract compatibility without
regenerating source during ordinary tests, including under Native AOT.

`AddMethodCache` decorates explicit registrations. Missing services require explicit `AutoRegister(lifetime)`
with exactly one accessible implementation in the selected module. Use `FromAssemblyOf<T>()` when multiple
modules offer the same interface. Cacheable late, open generic, AnyKey and unsupported closed bindings fail explicitly.

Built-in synchronous methods run a synchronous factory with synchronous single-flight. They never wait on an
async loader or async refresh. Async callers can join a synchronous load. A custom async-only manager bypasses
synchronous caching and emits a diagnostic once per method/container. Current distributed adapters do not expose
bounded native synchronous operations through this path, so these calls use L1/origin and report unavailable L2;
use async methods to share results through L2. Synchronous distributed tag mutations report unsupported invalidation
after the origin/local mutation; use async mutation methods for distributed invalidation.

The attribute-move code fix preserves semantic binding across scopes and files: attribute/type aliases are qualified as needed, and constant arguments are materialized before they leave an implementation class.

With C# 13 or newer, generated decorators preserve `allows ref struct` constraints when the cached values and arguments themselves are ordinary cacheable types. Potentially ref-like type parameters used as cache arguments or results receive MC0009 instead of broken generated code. The packages require .NET 10 and the .NET 10 SDK. C# 12 source remains supported when explicitly selected in that SDK.
