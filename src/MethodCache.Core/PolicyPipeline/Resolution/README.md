# Policy Resolution

Priority-based merging of configuration sources into unified cache policies.

## Key Components

### PolicyResolver
- **Core resolution engine** - Merges policies from multiple sources
- Applies priority rules (higher priority wins per field)
- Tracks provenance (which source set each field)
- Thread-safe and optimized for high performance

### PolicyRegistry
- Central registry of all resolved policies
- Keyed by method id (`Namespace.IService.Method`)
- Provides fast lookup during method invocations
- Supports dynamic updates from runtime sources

### PolicyRegistrationExtensions
- `EnsurePolicyServices()` registers the resolver, registry and `PolicyDiagnosticsService`

### PolicySourceRegistration
- Pairs a policy source with its priority
- Used by the resolver to determine merge order

## Resolution Algorithm

1. **Collect** - Gather policies from all registered sources for a method
2. **Sort** - Order by priority (ascending)
3. **Merge** - Field-by-field merge, higher priority wins
4. **Track** - Record provenance for each field
5. **Publish** - Update the method's `PolicyHandle`, which every decorator for the method shares

## Example

Given three sources for method `GetUserById`:

- Attribute: `[Cache(Duration = "00:05:00")]` (priority 10)
- Startup code: `.Configure(o => o.WithTags("user", "cache"))` (priority 40)
- Configuration file: `{ "Duration": "00:10:00", "Tags": ["user"] }` (priority 50)

**Resolved Policy:**
- Duration: `00:10:00` (from the configuration file, priority 50)
- Tags: `["user"]` (from the configuration file, priority 50)

**Provenance:**
- Duration: set by `attributes` and `configuration-files`; `configuration-files` wins
- Tags: set by `startup-fluent` and `configuration-files`; `configuration-files` wins

## Thread Safety

`PolicyResolver` is designed for concurrent access:
- Decorators read `PolicyHandle.Current` with one volatile read and no lock
- A change replaces the whole policy at once, so a call never sees half of an update
- Safe to call from multiple threads

## Performance

- Policies are resolved once per change, not per call or per resolve
- Runtime overrides and configuration reloads reach services that already exist, including singletons
- A version change gives the method new keys, so old entries are no longer read
- Two sources with the same priority both contribute; ties are ordered by source id
