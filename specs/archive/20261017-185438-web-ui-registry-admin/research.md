# Research: Web UI Analyst Registry Administration

All Technical Context items are resolved; no `NEEDS CLARIFICATION` remains.
Each decision cites the code or contract it rests on.

## R1. The Registry operations need OpenAPI schemas, not new operations

**Decision**: Describe the request and response bodies of the 13 existing
Registry operations in the OpenAPI document with internal DTO records in a new
`src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryContracts.cs`,
`.Accepts<T>("application/json")` on the four state changes with bodies,
`.Produces<T>()` (and `201` where the operation creates), and
`.ProducesProblem(...)` for the refusal statuses of the
[Registry API contract](../20261012-152148-analyst-registry/contracts/registry-api.md).
The endpoints serialize these records instead of anonymous objects; every
response body keeps exactly today's JSON property names, nesting, and values.
The committed snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json`
is refreshed, and the Web UI's Kiota client is regenerated from it by the
existing `npm run build`.

**Rationale**: the committed snapshot lists all 13 Registry operations
(`AnalystRegistryOpenApiTests`) but without any request or response schema,
because `AnalystRegistryEndpoints.cs` returns anonymous objects and reads
bodies by hand. Kiota therefore generates no models for them, and the Web UI
adapters would have to send and parse untyped JSON, unlike every other adapter
(`clubAdapter.ts`, `registrationAdapter.ts`). Other member operations already
follow the typed pattern (`AnalystManagerRegistrationContracts.cs`,
`.Produces<AnalystManagerRegistrationDto>()`). Documenting existing behavior
adds no operation, field, status, or rule (spec FR-019); integration tests
prove the bodies are unchanged.

**Alternatives considered**: untyped requests through `withJson` and manual
parsing of `ArrayBuffer` responses (no generated-client guarantee, drift
undetected by `OpenApiSnapshotTests`); new Web UI-specific read operations
(not needed: every read the spec requires exists).

## R2. The stored manifest and workflow definition in the schema

**Decision**: The workflow `definition` is described with typed records
(`requestedCapabilities`, `completionPolicy`, `segmentationPolicy`, `nodes`
with `nodeKey`, `profileId`, `profileVersion`, `profileDigest`, and `edges`
with `upstreamNodeKey`, `dependentNodeKey`, `kind`, optional `predicate` with
`kind`, `capabilityId`, `nodeKey`), because the platform builds it itself
(`DefineWorkflowHandler.BuildDefinition`, and the development seed with the
same shape). The image's `manifest` stays a free-form JSON object in the
schema: it is the stored `analyst-manifest.json`, owned and versioned by
`contracts/analysis/analyst-manifest/`, not by the API. The Web UI's Registry
adapter reads the declaration fields it shows (upstream, video access, output
schema version, compatible profiles, runtime, resources, requirements,
determinism) from that object tolerantly against manifest `1.0.0`, keeps
unknown values raw (FR-022), and shows the full manifest as text (FR-005).

**Rationale**: a typed definition gives the workflow views and the "start from
an existing version" prefill generated models; typing the manifest in the API
would duplicate the canonical manifest schema and couple the API document to
manifest versions.

**Alternatives considered**: typing the manifest as records (duplicates the
contract, drops unknown future fields); leaving the definition untyped (the
node and edge lists are the core of User Story 1 and 4).

## R3. Reading `ETag` and the created status in the Web UI

**Decision**: Add a Kiota middleware `ResponseMetadataHandler` and a request
option `ResponseMetadataOption` under `src/clients/web/src/platform/middleware/`,
and a helper `sendWithMetadataFor` in `platform/adapters/kiota.ts`. When a
request carries the option, the middleware records the response status and the
`ETag` header in it. The Registry adapter uses it to read the row version of
profile versions and of the default workflow (`ETag`, formatted by
`IfMatchHeader.Format`) for the next `If-Match`, and to tell `201 Created` from
`200 OK` for image registration and profile creation ("already registered" or
"already existed, nothing changed").

**Rationale**: unlike other member DTOs (club, team, match), the Registry
bodies carry no `version`; the API already sends the version as `ETag`
(`ProfileResult`, `DefaultResult`) and distinguishes created from existing by
status (`RegisterImageAsync`, `CreateProfileAsync`). Reading them on the
client keeps the API unchanged. The middleware follows the existing pattern of
`SessionOutcomeHandler` and `MinimumVersionHandler` in `platform/client.ts`.

**Alternatives considered**: adding `version` and `created` fields to the
response bodies (changes the API contract the spec keeps); Kiota's
`HeadersInspectionOptions` (exposes headers but not the status); a custom
`ResponseHandlerOption` (would bypass the shared error mapping).

## R4. Refusals, conflicts, and problem mapping

**Decision**: The Registry adapter translates Registry refusals into typed
outcomes before they reach `mapProblem`, without changing the shared mapping
used by other areas:

| Operation | Status and code | Outcome |
| --- | --- | --- |
| `registerAnalystImage` | `400`/`404`/`422`/`502`/`503` with the image refusal codes of [analyst-manifest-and-packaging.md](../20261012-152148-analyst-registry/contracts/analyst-manifest-and-packaging.md), details in `errors[]` (`field`, `code`) | `imageRefused` with code, details, and `retryable` (`registry-unavailable` only) |
| `createAnalystProfile` | `422 profile-contradicts-declaration` (`errors[]`: field and message), `404 image-not-registered`, `409 profile-content-exists`, `400 validation-failed` | `profileRefused` |
| `approveAnalystProfile`, `retireAnalystProfile` | `409 profile-not-draft`, `profile-in-default-workflow`, `profile-already-retired`; `412` | `transitionRefused`; `412` → `changed` |
| `defineWorkflow` | `422 workflow-invalid` (`errors[]`: `workflow`/violation code, or `nodes.<key>`/`profile-not-found`), `409 workflow-version-exists`, `400 validation-failed` | `workflowRefused` |
| `setDefaultWorkflow` | `422 workflow-not-resolvable`; `412`; `428` (a default exists but none was loaded) | `defaultRefused`; `412` and `428` → `changed` |
| `getDefaultWorkflow` | `404` | no default (not an error) |

`401`, `403`, offline, and server errors keep the shared handling
(`toPlatformFailure`, `FailureAlert`, session handling). Unknown codes are
shown with their raw value (FR-022).

**Rationale**: the codes come from `RegisterAnalystImage.cs`,
`AnalystProfileHandlers.cs`, `WorkflowHandlers.cs`, and
`AnalystRegistryEndpoints.cs`; workflow violations carry only codes (no
message), so each code gets its own translation key. `412` and `428` mean that
the record changed since it was loaded (FR-017).

## R5. Routes, navigation, and access

**Decision**: Routes under `/registry` (overview), `/registry/images`,
`/registry/images/register`, `/registry/images/:digest`,
`/registry/profiles`, `/registry/profiles/new` (with `?image=<digest>`),
`/registry/profiles/:profileId/versions/:version`, `/registry/workflows`,
`/registry/workflows/new` (with optional `?fromId=<workflowId>&fromVersion=<version>`), and
`/registry/workflows/:workflowId/versions/:version`, each wrapped in the
existing `clubAdminOnly` guard; one navigation entry `registry` after
`analystManagers` with `allow: isClubAdmin`. Filters (`capabilityId`,
`state`) are search parameters.

**Rationale**: matches `router.tsx` and `shell/navItems.ts`; `/registry` is
not used by the batch siblings (the analysis review uses
`/matches/:matchId/analysis-runs/:runId`).

## R6. Member names for registered, created, approved, retired, and changed by

**Decision**: The Registry views show the member's display name by resolving
the member id through the existing `memberAdapter` list (Club Admins may list
members); the seed actor (empty id) is shown as "Development seed", and an id
without a member is shown as "Unknown member" with the id.

**Rationale**: the Registry DTOs carry member ids only; no new read is needed.

## R7. Form defaults and client-side checks

**Decision**:

- Image registration checks before sending that the reference contains
  `@sha256:` followed by 64 hexadecimal characters (FR-011); the allowed
  registry host and prefix are checked only by the platform.
- The profile form takes capability, image, and model digests from the chosen
  declaration (read-only), prefills runtime constraints, accelerators, and
  resources from the manifest, the attempt, lease, stale-timeout, and
  execution-timeout values from the manifest's `requirements` minimums, input
  schema versions from the manifest's `upstream` entries, the output schema
  version from `output.schemaVersion`, and the video policy from
  `videoAccess`. Profile ids offered are those of profile versions with the
  same capability id; a new id is free text.
- The workflow form offers nodes from approved profile versions only; requested
  capabilities default to all node capabilities (subset allowed); the
  completion policy is a single-choice list with `all-requested-capabilities`;
  the segmentation policy is prefilled from the current default's definition;
  conditional edges offer the predicate kinds `capabilityRequested` (with a
  capability of the definition) and `nodeAccepted` (with a node of the
  definition). Prefill from an existing version flags nodes whose profile
  version is not approved and blocks submission (spec clarification).
- Approval confirmation names self-approval when `createdBy` equals the
  signed-in member.

**Rationale**: the defaults follow the declaration that the platform validates
against (`ProfileValidation`, `WorkflowValidation`); the platform stays the
authority, so the client checks only what avoids needless requests.

## R8. Online only and no stored Registry data

**Decision**: Registry reads use TanStack Query with in-memory caching only,
as the rest of the Web UI; nothing is persisted. The service worker already
passes every `/api/` request to the network (`serviceWorker/shellWorker.ts`);
a unit test asserts the Registry paths stay unhandled, and a simulated browser
test checks Cache Storage and browser storage after a session (FR-020).

## R9. Test strategy

**Decision**:

- Platform: Host test `AnalystRegistryOpenApiTests` gains a check that every
  Registry operation has request (where applicable) and response schemas;
  `OpenApiSnapshotTests` covers the refreshed snapshot; the existing Registry
  integration tests (`src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/`)
  keep passing and gain assertions that response JSON property names are
  unchanged.
- Web UI unit (Vitest): Registry adapter mapping (declaration, manifest parse,
  profiles, workflows, default), refusal translation, metadata middleware,
  form defaults and validation, digest shortening, components with Testing
  Library, translation keys, service-worker classification.
- Simulated browser (`--project simulated`): `registry.spec.ts` (overview,
  lists, details, register, create, approve, retire, define, default change,
  every refusal, stale conflicts) and `registry-access.spec.ts` (roles, axe,
  keyboard, offline, no stored data), with routes added to
  `tests/e2e/support/simulatedApi.ts`.
- Platform browser: a new chained Playwright project `platform-registry`
  (`tests/e2e/platform/registry.spec.ts`) after the last platform project,
  against the AppHost `e2e` mode: browses the seeded Registry, creates a
  profile version from a seeded declaration (with one execution value changed,
  so its content digest differs from the seeded profile), approves it, defines a workflow
  version equivalent to the seeded one with the new profile version, makes it
  the default, and registers a digest reference of a seeded declaration, which
  the platform answers as already registered without contacting an image
  registry (`RegisterAnalystImageHandler`). It runs last because it changes the
  default workflow.

**Rationale**: the AppHost has no OCI registry, so successful image
registration is evidenced in the simulated project and in the existing
platform integration tests (`OciRegistryContainerFixture`).
