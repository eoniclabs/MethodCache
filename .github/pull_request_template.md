Before: what a user of MethodCache sees today (or the bug, with the input that shows it).

After: what they see with this change.

**How**

How the change works, in a few lines. Name anything a reviewer should look at closely.

**Tested**

- [ ] A bug fix has a test that failed before the fix (say which).
- [ ] New code is covered by tests (CI fails below 80% of changed lines under `src/`).
- [ ] Time-dependent code is tested with a fake `TimeProvider`, not delays.
- [ ] A security-relevant change updates `docs/security.md` and its tests.

**Docs and API**

- [ ] Behavior changes update `README.md`, the page under `docs/features/` and the package's NuGet README.
- [ ] Public API changes are in `PublicAPI.Unshipped.txt`.
- [ ] `llms.txt` / `llms-full.txt` are regenerated when docs changed.
