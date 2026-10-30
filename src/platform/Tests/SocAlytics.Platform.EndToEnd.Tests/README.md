# SocAlytics Platform End-to-End Tests

Docker-backed end-to-end tests of the platform, the Analyst Manager, and the development Analyst images. They are
development evidence, not deployment support or production readiness. This README records the cause investigation of
the unreliable image push in the test support code, the design that makes the push reliable, and the commands that run
the tests. Every limit and value below is a development value; none is deployment support or production readiness.

## Cause

The investigation of the failure `The push did not report a manifest digest` and of the pushes that ended without a
result. All values are development evidence.

### History

`git log` over `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`, `src/analysts/`,
`src/platform/Tests/SocAlytics.Platform.Integration.Tests` (the registry fixture), and `.github/actions/` shows no
commit between the passing branch and the failing branch that changed the push, the registry fixture, or the Analyst
images: the only commits in these paths besides the commits of this spec are the Analyst Evaluation Harness (`dcc7994e`,
which adds a project under `src/analysts/capabilities/` and does not touch the end-to-end support code) and earlier
feature merges. A recent change on main is therefore not the cause.

### Push response shapes

Not recorded. The environment of this investigation has no reachable Linux Docker engine, so no body of a push, no
status line, and no observation of whether the `aux` record appears could be taken. A maintainer adds recorded
responses of a failing and a passing push together with the stability runs.

What the code shows: the former push support read a digest only from an `aux` record with a `Digest` property and
ignored the final status line `<tag>: digest: sha256:… size: …`, so a complete push whose response lacked the `aux`
record failed with the message above. The digest determination now takes the progress record, the final status line,
the local registry, and the engine's image record, in that order.

### Timeouts

- The `HttpClient` of `DockerEngine` had one request timeout of five minutes for every call, including the push. It now
  has an infinite client timeout, and every call ends at its own named limit (see [Limits](#limits)).
- The build goes through Testcontainers 4.15.0 (restored package, decompiled; it uses Docker.DotNet.Enhanced 4.3.3).
  Its image build (`DockerImageOperations.BuildAsync`) calls `BuildImageFromDockerfileAsync`, which makes a streamed
  request with an infinite timeout, and the underlying `HttpClient.Timeout` is infinite. The build client therefore
  applies no request timeout of its own to a build; only the cancellation token ends it.
- The request timeout of 100 seconds (`ClientOptions.Timeout`) of the same client applies only to its non-streamed
  requests (for example image inspection and deletion), not to the build. Testcontainers does not override it. Its
  image pull retries transient failures three times.

So the build is not shortened by a default of the build client, and a build limit has to come from the test's own
cancellation.

### Evidenced cause

- Digest from one source only: evidenced by the code. A complete push whose response has no `aux` digest record failed,
  although the final status line carried the digest.
- Load and the shared five-minute timeout: plausible for a push that ends incomplete, but not evidenced, because no
  timing or recording is available without a reachable engine. The classes of this project run one after another, so
  load comes from other assemblies and from the runner.
- A recent change on main: not evidenced; the history shows none.

The fix holds under either remaining cause: it determines the digest from every source in order, and it bounds and
retries the engine calls independent of why a push was slow or incomplete.

## Digest sources

`PushDigest` determines the manifest digest of a push from these sources, in this order, and the first valid one ends
the determination (a valid digest from the response causes no registry or engine lookup):

1. The `aux` progress record with a `Digest` property.
2. The final status line `<tag>: digest: sha256:… size: …` (it must agree with a progress record when both exist).
3. The manifest digest the local registry reports for the pushed tag.
4. The image record of the Docker engine.

If no source yields a digest, or the response carries an error or a mismatch, the failure message contains the full
response body and the outcome of each tried source.

## Limits

`EngineCallPolicy` names every limit and retry value. All are development values.

| Name | Value | Meaning |
| --- | --- | --- |
| `PushLimit` | 15 minutes | One push attempt |
| `BuildLimit` | 20 minutes | One image build attempt |
| `RegistryLookupLimit` | 30 seconds | One registry manifest lookup or engine image-record lookup |
| `MaxAttempts` | 3 | Attempts of a push or build, including the first |
| `RetryDelay` | 2 seconds | Delay between two attempts |

### Retry rule

- Only transient failures are repeated: an attempt that exceeds its own limit, an I/O or connection failure, and the
  HTTP statuses 429, 502, 503, and 504. Any other failure is deterministic and is tried once.
- At most `MaxAttempts` attempts run; the failure that ends them lists the reason of every attempt.
- Nothing is repeated after cancellation: the test's cancellation token always ends the call.
- A lookup of a service that is not yet answering is repeated within one limit instead of by attempts.

### Build and push bound

`AnalystImageCache.BuildTimeLimit` ends one cached build, including its wait for the gate: `(BuildLimit + PushLimit) *
MaxAttempts + RetryDelay * (MaxAttempts - 1)`, which is 105 minutes and 4 seconds. It is a guard, not a cause fix: the
tests of this assembly share one xunit collection and run one after another, so no concurrent load comes from them. The
bound only keeps a stalled build from blocking the run if the collection structure changes. `ImageBuildGate` has the same
purpose with `MaxConcurrentBuildsAndPushes` of 1: builds and pushes of the assembly run one at a time.

## Image reuse

- `AnalystImageCache` is an xunit class fixture. It owns the class's single `registry:2`, started on first use, and
  removes the class's images and disposes the registry when the class ends.
- One image variant (helper, mode or variant, and inputs) is built and pushed once per class. Concurrent tests that need
  the same variant share one build; a different variant or other inputs gets its own image and is never served a
  mismatching one. A failed build is reported to every waiting test and is not cached as a success.
- A test that changes or removes the image, or that must not share it, takes its own through the uncached
  `BuildAndPushAsync` of the helper and names the reason in a comment. Currently no test does: the tests that run the
  Docker runtime take their image from the fixture, and the simulated-runtime test of `SegmentMediaEndToEndTests` builds
  and pushes no image.

## Run commands

From the repository root. They need the .NET 10 SDK; the project run and the Images tests need a running Docker engine
on Linux, and a missing engine fails visibly.

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter "FullyQualifiedName~ImagePush"
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests
```

- The `ImagePush` filter runs the unit tests of the digest, limit, retry, gate, and reuse rules without Docker.
- The full project run and the Images tests are Docker-backed.

These commands and values are development evidence and not deployment support or production readiness.
