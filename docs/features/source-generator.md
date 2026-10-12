# Generated decorators

Automatic decoration preserves DI ownership: the container disposes type/factory registrations once and leaves application-supplied instances open. Generated `Add...WithCaching` factory helpers own the instances their factories return; create a new instance in those factories.

Put cache attributes on interface methods. The generator builds a decorator around the registered implementation
and routes cached calls through MethodCache. Your implementation stays ordinary application code.

The generator supports synchronous result methods, `Task<T>` and `ValueTask<T>`, overloaded methods, generic
interfaces and generic methods, including interfaces nested in generic containing types. Constraints on both
the containing type and the interface, including `unmanaged`, are preserved. Overloaded methods
include namespace-qualified parameter types in their identities so equally named types remain separate.

Public interface members are forwarded to the implementation: uncached methods, properties, indexers and events.
Non-public default helpers stay on the interface and are not emitted as decorator implementations. Non-public abstract contracts are rejected with `MCG013` because the decorator cannot call those members on the decorated instance.
Names that are C# keywords are escaped in the generated code, including namespaces, generic type parameters, constraints, property/event names and indexer
parameters. Parameters, generic parameters and overload names that resemble generator internals do not collide;
named calls through the interface keep its original parameter names. Property getters/setters and event add/remove accessors reach the original implementation.

Unsupported cached method shapes receive a [generator diagnostic](../user-guide/DIAGNOSTICS.md), with a code fix
to remove the cache attribute where available. Reference returns, ref struct results and pointer/function pointer
results are rejected before cached code is emitted. Those methods are forwarded when another method on the
interface is cacheable. See [keys](keys.md) for argument identity and
[configuration](configuration.md) for generated policy identifiers.

The end-to-end signature corpus under `tests/MethodCache.SourceGenerator.IntegrationTests` checks supported shapes
with C# 12 and the latest compiler: each generated decorator compiles and runs through the actual DI registration.

Invalidation methods preserve synchronous `ref` and `ref readonly` returns. `Task` and `ValueTask` invalidation methods may use `ref`, `out` or `in` parameters: tags are captured after the underlying call returns, and invalidation runs only after its asynchronous operation succeeds.

Factory registration helpers dispose owned implementations even when their service interface does not declare disposal. Asynchronous disposal prefers `IAsyncDisposable`, then falls back to `IDisposable`. Automatically discovered DI services retain container ownership.
