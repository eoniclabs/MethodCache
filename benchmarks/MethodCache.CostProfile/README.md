# Cost profile

The performance gate in [design-spec §10](../../docs/developer/design-spec.md#10-prestandagrind). It runs the
design harness cases (generated contract, HTTP response path, memory, L2 and HTTP workloads from
`MethodCache.Benchmarks`) and records what each operation costs:

- allocated bytes (median of seven batches),
- Redis commands, counted by the server from `INFO commandstats`, so commands inside Lua scripts count too,
- SQL commands with their text length and parameter count, from SqlClient's diagnostic events.

These counts are deterministic, so one run per revision is enough and a full comparison takes minutes, not
a timing series.

## Compare two revisions

```bash
export METHODCACHE_REDIS_URL=localhost:6379
export METHODCACHE_SQLSERVER_CONNECTION='Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=true'
benchmarks/MethodCache.CostProfile/cost-profile.sh <base-ref> [case filter]
```

The script checks out `<base-ref>` in a temporary worktree, copies this profile and the harness into it so both
sides run exactly the same cases, builds both and runs `compare.py`. Without the two variables the Redis and SQL
Server cases are skipped. Results and `report.md` go to `artifacts/cost-profile` (`COST_PROFILE_OUT`).

The gate fails when a case, compared with the base,

- sends more Redis or SQL commands per operation,
- writes to Redis on a hit, or
- allocates more bytes on a hit (more than 8 bytes and 0.5 %),

unless [accepted.json](accepted.json) accepts that case with a reason and upper bounds the new value stays within.
An accepted cost is a design decision and is reviewed like code.

## Find where the bytes come from

```bash
dotnet artifacts/cost-profile/head-bin/MethodCache.CostProfile.dll "<case>" --stacks 15 --out head-stacks.jsonl
```

`--stacks <seconds>` records the runtime's allocation ticks with call stacks (EventPipe) and attributes the case's
bytes per operation to the allocated type, the innermost frames and the innermost MethodCache frame. Run it with
`base-bin` too and compare the two files site by site.

## Time

```bash
benchmarks/MethodCache.CostProfile/cost-profile.sh --time <pairs> <base-ref> [case filter]
```

Times every matching case in its own process, `<pairs>` times per side, alternating which side goes first, with
the same short in-process BenchmarkDotNet job, and compares them pair by pair (`paired.py`). Cases timed one after
another in one process shape each other's JIT and GC state; in a trial here that alone made later cases look up to
30 % slower. A case is slower when the whole 95 % interval of the change/base ratio lies above 1 and the estimate is
more than 3 % above it; an interval wider than 6 % or a missing time fails too. A deliberate slowdown passes with a
`time` bound (the largest accepted ratio) in `accepted.json`. Each case and launch takes about 6 seconds, so filter
it. Run it on a quiet machine. The profile does not see a change that only costs CPU time, so §10 says when this
step is required.

The precision limit applies even to a faster result or an accepted slowdown. Both sides must contain every
launch; duplicate cases and non-finite times are rejected instead of being discarded or overwritten.
Timing records retain all 12 actual workload measurements, the runtime and GC mode. The exported time is
BenchmarkDotNet's time per operation, including its normalization for batched benchmarks; it is not divided again.
