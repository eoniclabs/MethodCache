# Generated decorators

Put cache attributes on interface methods. The generator builds a decorator around the registered implementation
and routes cached calls through MethodCache. Your implementation stays ordinary application code.

The generator supports synchronous result methods, `Task<T>` and `ValueTask<T>`, overloaded methods, generic
interfaces and generic methods. Generic constraints, including `unmanaged`, are preserved. Overloaded methods
include namespace-qualified parameter types in their identities so equally named types remain separate.

Other interface members are forwarded to the implementation: uncached methods, properties, indexers and events.
Names that are C# keywords are escaped in the generated code, including property/event names and indexer
parameters. Property getters/setters and event add/remove accessors reach the original implementation.

Unsupported cached method shapes receive a [generator diagnostic](../user-guide/DIAGNOSTICS.md), with a code fix
to remove the cache attribute where available. See [keys](keys.md) for argument identity and
[configuration](configuration.md) for generated policy identifiers.

The end-to-end signature corpus under `tests/MethodCache.SourceGenerator.IntegrationTests` checks supported shapes
with C# 12 and the latest compiler: each generated decorator compiles and runs through the actual DI registration.
