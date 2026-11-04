[← MethodCache documentation](../README.md)

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
Long namespace and type names also work with `EmitCompilerGeneratedFiles`: the generated files get short, stable
names.

Unsupported cached method shapes receive a [generator diagnostic](../user-guide/DIAGNOSTICS.md), with a code fix
to remove the cache attribute where available. Reference returns, ref struct results and pointer/function pointer
results are rejected before cached code is emitted. Those methods are forwarded when another method on the
interface is cacheable. See [keys](keys.md) for argument identity and
[configuration](configuration.md) for generated policy identifiers.

## Registration

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

`AddMethodCache` decorates the services you registered before it. A service you didn't register is registered only
with `AutoRegister(lifetime)`. The rules for keyed, generic, factory and several registrations are in
[Registration](../reference/registration.md).

## Synchronous methods

A synchronous `[Cache]` method loads synchronously: concurrent callers with the same arguments share one call, and it
never blocks on an async load. It uses the memory cache only, also when a distributed cache is configured, so it never
waits on the network; use an async method to share results between instances. A synchronous `[CacheInvalidate]`
method clears this instance's memory before it returns, and reaches the distributed cache in the background. With
your own `ICacheManager`, which has only async operations, synchronous methods aren't cached and a warning is logged
once per method.

## Language versions

C# 12 and newer are supported with the .NET 10 SDK. With C# 13 or newer, generated decorators keep `allows ref struct`
constraints on generic methods; a type parameter that may be a ref struct, used as an argument or result, is
[MC0009](../user-guide/DIAGNOSTICS.md#mc0009).
