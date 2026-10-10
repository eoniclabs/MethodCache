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
- Keyed by method signature
- Provides fast lookup during method invocations
- Supports dynamic updates from runtime sources

### PolicyRegistrationExtensions
- DI registration helpers for policy sources
- Simplifies adding custom sources
- Example: `services.AddPolicySource<MyCustomSource>()`

### PolicySourceRegistration
- Metadata about registered sources
- Priority, type, and lifecycle information
- Used by resolver to determine merge order

## Resolution Algorithm

1. **Collect** - Gather policies from all registered sources for a method
2. **Sort** - Order by priority (ascending)
3. **Merge** - Field-by-field merge, higher priority wins
4. **Track** - Record provenance for each field
5. **Publish** - Update the method's `PolicyHandle`, which every decorator for the method shares

## Example

Given three sources for method `GetUserById`:

- Attribute: `[MethodCache(Duration = 300)]` (Priority 10)
- ConfigFile: `{ "Duration": 600, "Tags": ["user"] }` (Priority 40)
- FluentApi: `cfg.WithTags("user", "cache")` (Priority 50)

**Resolved Policy:**
- Duration: `600` (from ConfigFile, priority 40)
- Tags: `["user", "cache"]` (from FluentApi, priority 50)

**Provenance:**
- Duration: Set by `ConfigFile` (40)
- Tags: Set by `FluentApi` (50)

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
