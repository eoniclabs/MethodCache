# Full .NET 10 functional checkpoint b518139

Exact head: b518139fc469144d599be4d9582fa4540d76c314, CI run
20261015-182702-b518139fc469. All Linux jobs and macOS/Windows build/unit jobs pass;
all three platform and aggregate statuses were verified success on 2026-10-15 at 16:37 UTC.

The main Linux test job passes 2,518 facts in 16 projects, zero failures and one intentional
opt-in soak skip. Required Redis/SQL, randomized properties, enabled soak, both cultures,
demo, diff coverage, AOT, installed packages and docs pass. `manifest.json` records each
exit and per-project count; original selected logs are retained per platform.

Shipped sources match runtime12bf99d and the current stack. This head predates the later
fixed-batch epoch fixture: current-head full CI still remains required. It also predates
unfinished PR132/SipHash choices and is not the final combined runtime or any timing pass.
