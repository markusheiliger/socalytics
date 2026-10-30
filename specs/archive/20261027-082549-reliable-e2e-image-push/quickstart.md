# Quickstart: Validating Reliable End-to-End Analyst Image Push

Run from the repository root. Details of the types are in [data-model.md](data-model.md), decisions in
[research.md](research.md).

## 1. Docker-free unit tests (no engine needed)

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter "FullyQualifiedName~ImagePush"
```

Expected: all pass without Docker: each recorded push-response shape yields the correct digest (record, status line
only, neither with the registry reporting it, neither with only the engine record), none/error/mismatch/malformed
fail with the full body and per-source results, a valid response digest causes no registry or engine lookup, a stalled
call ends at the named limit or the cancellation, a transient failure is repeated exactly `MaxAttempts` times with
every reason reported, a deterministic failure is tried once, no more than the gate's value of builds run at once, and
one image variant is built once per class with a faulted build reported to every waiting test.

## 2. Docker-backed end-to-end project and Images tests (Linux engine)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests
```

Expected: every test passes (33 of 33, or all if the count changed) in one full run, nothing skipped or loosened, and a
missing engine still fails visibly. Compare assertions and attributes with `git diff` against the base: none removed.

## 3. Documentation

```bash
node .github/scripts/check-markdown.mjs --files src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/README.md src/platform/README.md
```

Expected: no findings; the README names the evidenced cause, the digest sources in order, the limits and values, the
retry rule, the bound, the reuse rule, and these commands.

## 4. Manual stability evidence (not a task gate)

Five consecutive full runs of step 2, two of them after a Docker engine restart and one under CPU load, recorded in
[research.md](research.md) `## Evidence`.
