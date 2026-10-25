# Quickstart: validating key rotation

Prerequisites: Docker-compatible runtime (PostgreSQL, NATS), .NET 10 SDK, SoftHSM2 on
Linux (Manager tests). Commands run from the repository root.

1. Build and restore:
   `dotnet restore src/platform/SocAlytics.Platform.slnx && dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`
   and the same for `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`.
2. Contract fixtures: `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`
   (schema, examples, and fixture vectors under `contracts/analyst-manager/key-rotation/v1/`).
3. Platform behavior: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~KeyRotation"`.
   Expect: identity unchanged, new fingerprint in the fleet read, old token/proof/queue
   credentials refused, every refusal reason audited, concurrent rotations switch once,
   rate limit returns `429`, administrator and member sessions refused.
4. Manager behavior: `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build --filter "FullyQualifiedName~KeyRotation"`.
   Expect: interruption at each step (after key creation, before and after the platform
   switch, during the file replace) converges on one key without re-registration; the CLI
   reports "no Manager running" with no change when nothing answers local control.
5. End to end (Linux engine):
   `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~KeyRotation"`.
6. API contract: refresh the OpenAPI snapshot only in a development checkout with
   `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`.
7. Documentation: `node .github/scripts/check-markdown.mjs`.

Manual only: Windows CNG and macOS key stores (as for registration).
