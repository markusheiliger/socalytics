# SocAlytics Platform End-to-End Tests

Docker-backed end-to-end tests of the platform, the Analyst Manager, and the development Analyst images. They are
development evidence, not deployment support or production readiness. This README records the cause investigation of
the unreliable image push in the test support code; the run commands and the named limits follow with the
documentation of the finished design.

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

- The `HttpClient` of `DockerEngine` has one request timeout of five minutes for every call, including the push.
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
