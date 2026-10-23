# Windows validation

The previous GPU-initialization run recorded 108 Rust, 72 headless UI, 39 engine, 38 Python and 19 packaging test passes, with one host real-worker integration test explicitly skipped. Installed app/API checks ran in Sandbox and WSL.

Those counts describe that earlier source/package snapshot. The present readiness work changes packaging, documentation and model metadata, and must record fresh test results rather than inherit a pass. See [the readiness ledger](readiness.md) and [native acceptance](native-validation.md).

Raw historical reports, package hashes, failures and benchmark commands are preserved locally in ignored evidence folders. No commit, push or publication is implied by these checks.
