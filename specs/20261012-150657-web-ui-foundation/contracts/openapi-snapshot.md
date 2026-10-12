# Contract: OpenAPI Snapshot

- **File**: `src/platform/SocAlytics.Platform.Api/openapi/v1.json`, the
  normalized output of `GET /openapi/v1.json`: keys sorted, no `servers`
  entry, LF line endings, and a trailing newline.
- **Owner**: the platform API. The file changes in the same commit as the
  endpoints that change it.
- **Test**: `src/platform/Tests/SocAlytics.Platform.Host.Tests/OpenApiSnapshotTests.cs`
  starts the API as the Host tests do, fetches the document, normalizes it, and
  fails with a short diff and the instruction to regenerate when it differs.
  With `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1`, the test rewrites the file
  instead of failing (developer convenience; never set in the verification
  environment).
- **Consumer**: the Web UI `build` script generates the TypeScript client from
  this file with Kiota 1.36.0 into `src/clients/web/src/generated/platform-api/`
  (ignored).
- **Verification**: a change under `src/platform/SocAlytics.Platform.Api/`
  runs the platform check (snapshot test) and the Web UI check (generation and
  type checking), per the Web UI verification environment.
