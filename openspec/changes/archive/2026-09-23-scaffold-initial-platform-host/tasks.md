# Initial Platform Host Tasks

## 1. Platform Project Foundation

- [x] 1.1 Owner: soca-developer. Add the .NET 10 SDK policy, central build and package settings, `SocAlytics.Platform.slnx`, and the production and test project files defined by the design, with every production project as a direct child of `src/platform`; verify `dotnet restore src/platform/SocAlytics.Platform.slnx` succeeds and the solution lists only the intended peer projects.
- [x] 1.2 Owner: soca-developer. Implement `SocAlytics.Platform.Club`, `SocAlytics.Platform.IdentityAccess`, `SocAlytics.Platform.Recordings`, `SocAlytics.Platform.Registry`, `SocAlytics.Platform.Analysis`, and `SocAlytics.Platform.AgentOrchestration` with one public dependency-injection composition boundary per capability and internal implementation markers, without capability-to-capability project references or a generic shared project; verify all six capability projects build with warnings treated as errors.

## 2. Executable Host And Composition

- [x] 2.1 Owner: soca-developer. Implement `SocAlytics.Platform.ServiceDefaults` with the standard Aspire service-discovery, resilience, OpenTelemetry, liveness, and readiness wiring; verify the ServiceDefaults project builds and exposes the shared registration and endpoint-mapping extensions used by the API.
- [x] 2.2 Owner: soca-developer. Implement the dependency-free ASP.NET Core API host, register all six modules through their public boundaries, map `/alive` and `/health`, and publish the built-in `v1` document at `/openapi/v1.json` without adding a domain or sample endpoint; verify the API project builds and its endpoint inventory contains only the operational and OpenAPI surface.
- [x] 2.3 Owner: soca-developer. Implement the Aspire AppHost with the API as its sole resource and an API health wait condition, without PostgreSQL, NATS, S3, or container resources; verify the AppHost project builds and a local launch reports the API resource healthy.

## 3. Executable Evidence

- [x] 3.1 Owner: soca-developer. Add the xUnit v3 and Shouldly host smoke test using Aspire hosting test support to launch the AppHost, wait for the API, verify successful `/alive` and `/health` responses, validate `/openapi/v1.json` as the `v1` OpenAPI document, and confirm all six module registrations execute; verify the focused host test project passes.
- [x] 3.2 Owner: soca-developer. Add NetArchTest.Rules, project-reference, and reflection assertions that reject cross-capability implementation dependencies, capability-to-capability project references, API access beyond public capability composition types, and accidental public capability implementation types; verify the focused architecture test project passes and a deliberate local violation makes the relevant assertion fail before reverting that probe.

## 4. Documentation Synchronization

- [x] 4.1 Owner: soca-developer. Update `README.md` and `src/platform/README.md` with the exact restore, build, test, and AppHost run commands plus explicit deferred-scope statements; verify every documented command matches the implemented paths and all changed relative links resolve.
- [x] 4.2 Owner: soca-architect. Synchronize `docs/architecture/platform-implementation.md` and `AGENTS.md` so the six planned `SocAlytics.Modules.*` assemblies are renamed to their `SocAlytics.Platform.*` capability projects, their peer layout under `src/platform` is recorded, and current-state language records only the executable host evidence delivered by this change while preserving unresolved infrastructure, security, client, and production evidence; verify the narratives match the implemented names and paths and do not claim an ADR, domain behavior, infrastructure integration, deployment support, or production readiness.

## 5. Independent Review And Verification

- [x] 5.1 Owner: soca-auditor. Independently review the scaffold for unintended data exposure, domain endpoints, external infrastructure, secrets, authentication claims, deployment claims, or security-control claims and record any findings for owner-routed remediation; verify the audit explicitly distinguishes development health/OpenAPI exposure from an approved production ingress posture.
- [x] 5.2 Owner: soca-verifier. Independently verify the implementation against every `platform-host` scenario by running restore, build, the complete test suite, a bounded AppHost startup check, Markdown diagnostics, relative-link validation, `openspec doctor --json`, `openspec schema validate spec-driven --json`, `openspec validate --all --json`, and `openspec status --all --json`; report failures without authoring remediation and confirm the repository documentation matches the evidence observed during verification.
