# Generated decorators

Put cache attributes on interface methods. The generator builds a decorator around the registered implementation
and routes cached calls through MethodCache. Your implementation stays ordinary application code.

The generator supports synchronous result methods, `Task<T>` and `ValueTask<T>`, overloaded methods, generic
interfaces and generic methods, including interfaces nested in generic containing types. Constraints on both
the containing type and the interface, including `unmanaged`, are preserved. Each overload has its own
[method id](configuration.md#appsettingsjson) and its own cache entries.

Public interface members are forwarded to the implementation: uncached methods, properties, indexers and events.
Non-public and sealed default interface methods stay on the interface with their own bodies; the decorator doesn't
implement them. An interface with non-public abstract members is rejected with
[MCG013](../user-guide/DIAGNOSTICS.md#mcg013), because the decorator can't call those members on the implementation.

Keywords and unusual names are escaped, and your own types named like framework types (such as `Task` or `List`)
don't clash with the generated code. You don't need to rename anything.

Unsupported cached method shapes receive a [generator diagnostic](../user-guide/DIAGNOSTICS.md), with a code fix
to remove the cache attribute where available. Reference returns, ref struct results and pointer/function pointer
results are rejected before cached code is emitted. Those methods are forwarded when another method on the
interface is cacheable. See [keys](keys.md) for argument identity and
[configuration](configuration.md) for generated policy identifiers.

## Registration

`AddMethodCache()` wraps every registration of a cached interface in its decorator, and keeps the container's
ownership as it is: the container disposes the implementations it creates, and leaves instances you registered
yourself open. To register a cached interface with a factory instead, use the generated helpers described in
[Getting started](../getting-started.md#registering-with-a-factory).

The end-to-end signature corpus under `tests/MethodCache.SourceGenerator.IntegrationTests` checks supported shapes
with C# 12 and the latest compiler: each generated decorator compiles and runs through the actual DI registration.
