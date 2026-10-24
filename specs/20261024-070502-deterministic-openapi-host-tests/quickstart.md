# Quickstart: Validating Deterministic OpenAPI Host Tests

Prerequisites: .NET 10 SDK from `src/platform/global.json`, a running Docker-compatible runtime, and a
restored and built solution:

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
```

## 1. Establish the cause (before changing anything)

Run the flaky test 20 consecutive times alone and the whole project 5 consecutive times under processor
load (`stress-ng --cpu $(nproc)`, or `$(nproc)` busy-loop processes as the fallback), each driven by one shell
loop that stops at the first failure, and record the failures and the elapsed times (see
[research.md](research.md) for the hypotheses). Also examine the failed verification logs of #201, #225, and #275,
or record that `gh` or the logs were unavailable. Only after these runs and logs may a hypothesis be
recorded as "not reproducible":

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "FullyQualifiedName~OpenApiDocumentContainsAllAnalystRegistryOperationIds"
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build
```

Expected: a verdict (proved, ruled out, or not reproducible) for each hypothesis, with logs of the failed
verifications of #201, #225, and #275 as the primary evidence.

## 2. After the fix: determinism (SC-001)

Repeat the single-test command at least 20 times and the whole-project command at least 5 times, the latter
under processor load. Produce the load with `stress-ng --cpu $(nproc)` (or `$(nproc)` busy-loop processes) for the whole duration of the runs and record the tool and level in the Evidence section of [research.md](research.md). Expected: every run passes and the test count is unchanged.

## 3. Probes (SC-002)

With temporary, uncommitted changes: remove an Analyst Registry operation from the served document, and make
the stamp unable to become ready. Expected: the first fails on the `ShouldContain` assertion, the second
fails every dependent test with the start error, both within the existing limits. Remove the probes.

## 4. Unchanged surface (SC-003, FR-006)

```bash
git diff --stat -- src/platform/SocAlytics.Platform.Api/openapi/v1.json
git diff -U0 -- src/platform/Tests/SocAlytics.Platform.Host.Tests | grep -E '^-.*(ShouldBe|ShouldContain|ShouldNotBe|ShouldBeTrue|Assert\.)'
node .github/scripts/check-markdown.mjs
```

Expected: the snapshot is unchanged, no assertion line is only removed (a moved assertion appears on both
sides), and Markdown validation passes.
