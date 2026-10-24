# Implementation Plan: Analyst Manager Update Feed

**Branch**: `20261023-080504-manager-update-feed` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-080504-manager-update-feed/spec.md`

## Summary

The platform serves one DPoP-authenticated read operation, `GET /api/v1/analyst-managers/self/update-offer`, that
tells a Manager which verified Manager release its stamp offers. The offer is computed from a release source
abstraction (GitHub Releases in production, a controllable fake in Development and Test), filtered by the stamp's
channel (`stable`, or `preview` including stable), an optional pinned version, and the stamp's minimum version, and
contains only releases whose SBOM, build-provenance attestation, and package hashes and sizes verified. Verified
releases are cached 15 minutes and failures 1 minute (platform options, validated at start, applied on reload).
Refusals are counted in a metric and logged. The Manager asks for the offer at its existing periodic check and shows an
"update available" notice (status window, tray menu, CLI `status`) only when the offer is newer than its build; it
never downloads or applies anything.

Technical approach: a new `Registry/UpdateFeed` folder in the Application layer (options, settings provider that
mirrors `MinimumManagerVersionProvider`, offer selection, handler, metrics, `IManagerReleaseSource`),
implementations in Infrastructure (fake source; GitHub source and attestation verifier), one endpoint in
`AnalystManagerSelfEndpoints`, a new shared contract `contracts/analyst-manager/update-offer/v1`, and Manager changes
in `PlatformClient`, `RegistrationStatusMonitor`, a new in-memory `UpdateOfferState`, the status snapshot, the
local-control schema, and the view models.

## Technical Context

**Language/Version**: C# on .NET 10 (platform, `src/platform/global.json`; Manager, `src/analysts/manager/global.json`)

**Primary Dependencies**: existing only (ASP.NET Core, `Microsoft.Extensions.Options`, `System.Diagnostics.Metrics`,
`System.Security.Cryptography` for DSSE and certificate-chain verification, `HttpClient`); no new package. Avalonia
stays the Manager UI.

**Storage**: None. The offer is not stored per registration; verified releases and failures are cached in memory per
API instance.

**Testing**: xUnit v3. Platform: `Host.Tests` (options, OpenAPI), `Integration.Tests/Registry` (offer selection,
cache, verification with fixture bundles signed by a test CA, authorization through the existing machine-authentication
harness, metrics), `Contracts.Tests` (schema and golden fixtures), `EndToEnd.Tests` (Manager against the platform with
the fake source). Manager: `SocAlytics.Analysts.Manager.Tests` (state, polling, snapshot, CLI, local-control schema,
view models, no-side-effect checks). No test contacts GitHub.

**Target Platform**: Linux server container (platform); Windows, macOS, and Linux desktop plus the headless Linux
server profile (Manager).

**Project Type**: polyglot monorepo: platform web service plus desktop and headless Manager.

**Performance Goals**: one cached lookup per Manager check (default interval 60 s); a cache hit never reaches the
release source or repeats verification.

**Constraints**: development cache lifetimes 15 minutes (verified) and 1 minute (failed); a release source failure
never affects other operations; no outbound GitHub access in Development and Test; no package bytes are downloaded,
stored, or served; the offer carries no registration data.

**Scale/Scope**: one offer per stamp; the release source returns at most the latest 30 releases per evaluation.

All Technical Context items are resolved; see [research.md](research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / constraint | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements [Updates](../../docs/architecture/analyst-manager.md#updates) as written; the Architecture Updates section below lists the decisions added and the statements to correct. |
| II. Source-area ownership | Pass | Platform code under `src/platform/`, Manager code under `src/analysts/manager/`, the shared contract in repository-root `contracts/`; no new `src/` child. |
| III. API-first control plane | Pass | One `/api/v1` operation with OpenAPI and JSON Schema contracts; metadata only, no package bytes; the Manager is an API consumer. |
| IV. Evidence over claims | Pass | Focused tests in `Host.Tests`, `Integration.Tests`, `Contracts.Tests`, `EndToEnd.Tests`, and the Manager tests ([quickstart.md](quickstart.md)); nothing is presented as production readiness. |
| V. Focused, minimal changes | Pass | Reuses `ManagerVersion`, the `MinimumManagerVersionProvider` convention, the Manager self endpoints, `ProblemResults`, and the queue-depth polling slot; no new dependency. |
| Environment actions | Pass | Needs .NET 10 and the existing Docker-backed end-to-end tooling only; no new framework, SDK, or tool, so no environment spec dependency. |
| Unresolved decisions explicit | Pass | Production outbound GitHub access, attestation trust roots and identities, channel values, publisher identity, release workflow, signing, download, and apply stay open and documented as such. |

No violations.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-080504-manager-update-feed/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── README.md
│   └── update-offer.md
└── tasks.md             # /speckit-tasks, not created here
```

### Source Code (repository root)

```text
contracts/analyst-manager/update-offer/v1/
├── update-offer.schema.json                 # new shared contract, version 1.0.0
└── examples/{valid,invalid}/                # golden fixtures
contracts/README.md                          # catalog row

src/platform/
├── SocAlytics.Platform.Application/
│   ├── Registry/UpdateFeed/                 # UpdateFeedOptions, UpdateFeedSettingsProvider, IManagerReleaseSource,
│   │                                        #   ManagerRelease, ReleaseVerificationResult, ReleaseOfferSelector,
│   │                                        #   ReadUpdateOfferHandler, ReleaseVerificationCache
│   ├── Telemetry/UpdateFeedMetrics.cs       # counter socalytics.registry.update_feed_refusals
│   └── ApplicationServiceCollectionExtensions.cs
├── SocAlytics.Platform.Infrastructure/
│   ├── Registry/UpdateFeed/                 # FakeManagerReleaseSource, GitHubManagerReleaseSource,
│   │                                        #   AttestationBundleVerifier
│   └── InfrastructureServiceCollectionExtensions.cs
├── SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs   # getAnalystManagerUpdateOffer
├── SocAlytics.Platform.Api/appsettings.Development.json              # Fake source in Development
└── Tests/                                   # Host, Integration, Contracts, EndToEnd additions

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/
│   ├── Platform/{PlatformClient.cs,PlatformWorkContracts.cs}   # GetUpdateOfferAsync
│   ├── Registration/{UpdateOfferState.cs,RegistrationStatusMonitor.cs}
│   ├── Diagnostics/ManagerStatusSnapshot.cs                    # UpdateAvailableStatus
│   ├── Hosting/{ManagerControl.cs,ManagerServiceCollectionExtensions.cs}
│   ├── Cli/ManagerCli.cs
│   └── LocalControl/LocalControlServer.cs
├── SocAlytics.Analysts.Manager/{ViewModels,Tray}/              # status window and tray menu notice
├── contracts/local-control.schema.json                         # additive optional updateAvailable
└── Tests/SocAlytics.Analysts.Manager.Tests/
```

**Structure Decision**: the feed is a functional area of the Registry inside the existing layers (folders and
namespaces, not projects); the offer is a Manager self operation beside `queue-depth`; the contract is
repository-root so platform and Manager tests share it, like `queue-depth`.

## Architecture Updates

### Narratives

- [Analyst Manager: Updates](../../docs/architecture/analyst-manager.md#updates): the stamp-served feed, channels,
  pinning, attestation verification, and the interplay with the minimum version (implemented here; download and
  self-update stay planned).
- [Analyst Manager: Machine Authentication](../../docs/architecture/analyst-manager.md#machine-authentication): the
  offer is one more DPoP-authenticated Manager self operation.
- [Platform Implementation](../../docs/architecture/platform-implementation.md) (Analyst Manager section on installers
  and updates) and [Analysts, Models, and Hardware](../../docs/architecture/analysts-models-and-hardware.md) (the
  Velopack spike scope).

### Design updates

Applied now to `analyst-manager.md#updates`, worded as decided but not implemented; the implementation turns them
into present tense:

- The `preview` channel offers the highest verified version among preview and stable releases; `stable` offers only
  stable releases; the stamp never offers a release below the minimum.
- Settings `Registry:UpdateFeed:Channel` (default `stable`) and `Registry:UpdateFeed:PinnedVersion` are validated at
  start and applied on reload; one offer for the whole stamp through
  `GET /api/v1/analyst-managers/self/update-offer`; verified releases cached 15 minutes and failures 1 minute
  (development values); an unverified release is never offered.

The implementation also adds to the narrative: the fake release source for Development and Test, the refusal metric
`socalytics.registry.update_feed_refusals` (no registration identifiers, no security audit event), and the Manager's
notice-only "update available" (distinct from the minimum-version notice).

### Status statements at merge

- `analyst-manager.md`, Updates, the "This decision is not implemented yet." sentence just added: remove it.
- `analyst-manager.md`, Updates, "The minimum version is implemented ahead of the update feed; the feed, installers,
  channels, pinning, attestation checks, and self-update do not exist yet, so an operator updates a Manager by
  installing a newer build by hand": the feed, channels, pinning, and attestation checks are implemented as
  development evidence; installers, package download, background download, draining, and self-update still do not
  exist, so the operator still installs a newer build by hand after the Manager shows the notice.
- `analyst-manager.md`, Updates, "By default, the Manager downloads an update in the background ...": stays a
  planned behavior; state that the Manager currently only shows the notice. "The stamp needs outbound access to
  GitHub for the feed": production access remains an open deployment decision.
- `analyst-manager.md`, Updates, "The Manager verifies package hashes, and signatures once releases are signed":
  stays planned (Manager-side verification is out of scope).
- `analyst-manager.md`, the Velopack spike paragraph ("... and the stamp-served update feed"): the feed is no longer
  part of the spike; the spike proves self-update against it.
- `platform-implementation.md`: "the feed, channels, and pinning stay planned after the Velopack spike": the feed,
  channels, pinning, and attestation verification are implemented; installers and Velopack stay planned.
- `analysts-models-and-hardware.md`: "and the stamp-served update feed with attestation checks and drain before
  applying": the feed and its checks exist; the spike covers the drain and apply.
- `src/platform/README.md` ("The update feed, installers, and self-update do not exist yet"),
  `src/analysts/manager/README.md` (the two statements that updates, attestation, and the feed do not exist), and
  `src/analysts/README.md` if it repeats them: describe the feed, the options, and the notice as development evidence.

The documentation task MUST make the listed narratives and every status statement the feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time (the rewrites above are examples, not the whole
job), keeping installers, download, apply, Manager-side signature verification, the publisher identity, the release
workflow, and production values explicitly unimplemented.

## Phase 0 and Phase 1 Outputs

- [research.md](research.md): decisions on operation shape, selection, verification, caching, release source
  configuration, Manager integration, metrics, and contracts.
- [data-model.md](data-model.md): options, in-memory entities, selection rules, Manager state.
- [contracts/](contracts/README.md): operation and contract description; schemas are authored in the repository-root
  `contracts/` folder during implementation.
- [quickstart.md](quickstart.md): runnable validation.

**Post-design Constitution Check**: unchanged, all gates pass. The one design risk, production attestation
verification without a GitHub or Sigstore SDK, is contained by a verifier abstraction, configured trusted roots and
identities (unset by default, so the production source offers nothing until a deployment decides them), and
fixture-based tests.

## Complexity Tracking

No violations to justify.
