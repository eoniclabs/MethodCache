# Reviewed generated contract 1 before release

This .NET 10 baseline replaces the unpublished development checkpoints for the first release.
It covers primitive, record, collection and provider parts through a bound KeyBuilder,
as well as fallback, generic/versioned calls, invalidation and third-party interfaces.

Freeze explicitly with `build/ci/freeze_generated_contract.py --source-commit <full HEAD> --baseline-directory Contract1Final`.
Ordinary builds never regenerate images. Earlier directories remain historical snapshots;
they carry no compatibility promise because none of these packages has been released.
