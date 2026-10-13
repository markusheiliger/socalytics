# Contract: Development Object Storage Access

Development settings only; production storage, CORS, and origins remain
unresolved (`platform-implementation.md` § Object Storage).

## AppHost (`src/platform/SocAlytics.Platform.AppHost/Program.cs`)

| Resource | Setting | Value |
| --- | --- | --- |
| `rustfs` | `RUSTFS_CORS_ALLOWED_ORIGINS` | The Web UI endpoint's origin (scheme, host, port), in both the default and the `e2e` mode |
| Web UI | `SOCALYTICS_OBJECT_STORAGE_ORIGIN` | The `rustfs` `s3` endpoint's origin, used by `vite.config.ts` for `connect-src` |

The browser's preflight for a part `PUT` must succeed for method `PUT` and
request header `x-amz-checksum-sha256` from the Web UI origin, and must fail
for any other origin.

**Fallback** (research.md § R11): if RustFS `1.0.1` does not honor the variable
for presigned `PUT`, `DevelopmentBucketInitializer` applies one S3
`PutBucketCors` rule (allowed origin from `ObjectStorage:DevelopmentCorsOrigins`,
method `PUT`, header `x-amz-checksum-sha256`, max age 600 s) when
`EnsureBucketOnStartup` is `true`, and the AppHost sets that option instead.

## Verification

- `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs`
  asserts both settings in the AppHost model.
- `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/DevelopmentCorsTests.cs`
  starts RustFS `1.0.1` with the allow-list and proves the preflight for the
  allowed origin and its refusal for another origin, before any browser test
  depends on it; it decides between the variable and the fallback.
- The `platform` browser project uploads through the Web UI origin, which
  fails without a working preflight.

## Simulated tests

`tests/e2e/support/fakeObjectStorage.mjs` (research.md § R14) listens on a
fixed local port, answers preflights for the preview origin only, verifies each
part's `x-amz-checksum-sha256` against the body, and exposes a control
endpoint for fault injection and inspection. The `simulated` preview server
receives its origin as `SOCALYTICS_OBJECT_STORAGE_ORIGIN`.
