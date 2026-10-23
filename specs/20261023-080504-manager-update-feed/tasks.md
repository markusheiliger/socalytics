# Tasks: Analyst Manager Update Feed

**Input**: Design documents from `/specs/20261023-080504-manager-update-feed/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/update-offer.md](contracts/update-offer.md), [quickstart.md](quickstart.md)

**Tests**: Included. The plan and quickstart require focused evidence in `Host.Tests`, `Integration.Tests`,
`Contracts.Tests`, `EndToEnd.Tests`, and the Manager tests. No test contacts GitHub.

**Organization**: Tasks are grouped by user story. Consecutive `[P]` tasks under one heading run in parallel, each from the same commit, so they change disjoint files.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Runs in parallel with the consecutive `[P]` tasks under the same heading, each from the same commit (disjoint files, no dependency on them)
- **[Story]**: User story of the task (US1 to US5); none for Setup, Foundational, and Polish

## Path Conventions

- Platform: `src/platform/SocAlytics.Platform.<Layer>/`, tests in `src/platform/Tests/`
- Update feed folder: `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/` (Application) and
  `src/platform/SocAlytics.Platform.Infrastructure/Registry/UpdateFeed/` (Infrastructure)
- Manager: `src/analysts/manager/SocAlytics.Analysts.Manager.Core/`, `src/analysts/manager/SocAlytics.Analysts.Manager/`,
  tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/`
- Shared contract: `contracts/analyst-manager/update-offer/v1/`

## Phase 1: Setup (Shared Contract)

**Purpose**: The machine-readable contract that the platform and the Manager tests share (FR-005).

- [ ] T001 Create the JSON Schema 2020-12 `contracts/analyst-manager/update-offer/v1/update-offer.schema.json` (version `1.0.0`) per [contracts/update-offer.md](contracts/update-offer.md): `additionalProperties: false` everywhere, `release` present exactly when `available` is `true`, `reason` present exactly when it is `false` (`no-release-in-channel`, `no-verified-release`, `no-release-satisfies-minimum`, `no-release-for-pin`), `version` as `MAJOR.MINOR.PATCH`, stamp and release `channel` as `stable` or `preview`, packages with `name`, `sizeBytes` (integer at least 1), `sha256` (64 lowercase hex), at least one package, `releaseNotesUrl` as `https` URI

### Golden fixtures

- [ ] T002 [P] Create the valid golden fixtures in `contracts/analyst-manager/update-offer/v1/examples/valid/` (available stable offer, available preview-channel offer serving a stable release, one file per reason class)
- [ ] T003 [P] Create the invalid golden fixtures in `contracts/analyst-manager/update-offer/v1/examples/invalid/` (unavailable with a `release`, available without `release`, malformed version, package without hash, non-hex hash, unknown property, unknown reason)

### Catalog and contract tests

- [ ] T004 Add the catalog row for `analyst-manager/update-offer/v1/update-offer.schema.json` (owner Registry and Analyst Manager, version `1.0.0`, fixtures, both test commands) to `contracts/README.md`
- [ ] T005 [P] Register the contract in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/ContractCatalog.cs` as the other contracts are and add `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/UpdateOfferContractTests.cs` (valid examples validate, invalid ones fail) modelled on `QueueDepthContractTests.cs`
- [ ] T006 [P] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/GoldenFixtures/UpdateOfferFixtureTests.cs` validating the same fixtures, modelled on `QueueDepthFixtureTests.cs`

---

## Phase 2: Foundational (Platform Feed Core)

**Purpose**: Types, options, cache, and the fake release source that every story builds on. No endpoint yet.

**CRITICAL**: No user story starts before this phase is complete.

### Types, options, metrics

- [ ] T007 [P] Create the release model in `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/`: `ManagerRelease.cs` (`Version` text, `Channel`, `ReleaseNotesUrl`, `Packages` with `Name`, `SizeBytes`, `Sha256`, `SbomReference`, `AttestationReferences`, immutable identity used as cache key), `ReleaseVerificationResult.cs` (`Verified` or `Refused` with reason class `sbom-missing`, `attestation-missing`, `attestation-invalid`, `subject-mismatch`, `package-size-mismatch`, `package-unverifiable`, `version-malformed`), `ReleaseOffer.cs` (`Available` with release, or unavailable with reason `no-release-in-channel`, `no-verified-release`, `no-release-satisfies-minimum`, `no-release-for-pin`, plus `observedAt` and effective channel), `IManagerReleaseSource.cs` (lists at most the latest 30 releases, with a verification operation that returns `ReleaseVerificationResult`), `IReleaseAttestationVerifier.cs` (the verifier abstraction that the source's verification operation calls, returning `ReleaseVerificationResult`; implemented by T032, so T033 depends only on this interface)
- [ ] T008 [P] Create `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/UpdateFeedOptions.cs` for section `Registry:UpdateFeed` per [data-model.md](data-model.md): `Channel` (`stable` default), `PinnedVersion` (empty), `VerifiedCacheLifetime` (15 minutes), `FailureCacheLifetime` (1 minute), `Source` (`None` default, `Fake`, `GitHub`), `GitHub` subsection (owner, repository, API base address, trusted root, expected identity; all empty)
- [ ] T009 [P] Create `src/platform/SocAlytics.Platform.Application/Telemetry/UpdateFeedMetrics.cs`: counter `socalytics.registry.update_feed_refusals` on meter `SocAlytics.Platform.Registry`, tag `reason` only, following `AnalysisMetrics.cs`

### Settings and fake source

- [ ] T010 [P] Create `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/UpdateFeedSettingsProvider.cs` and `UpdateFeedOptionsValidator.cs` mirroring `Registry/ManagerVersions/MinimumManagerVersionProvider.cs`: validate at start (channel neither `stable` nor `preview`, pin not `ManagerVersion.TryParse`, non-positive cache lifetime, each failing with a message that names the option; a pin below `Registry:ManagerVersion:Minimum` names both options), read through `IOptionsMonitor`, keep the last valid value after an invalid reload with one logged warning per invalid state, treat a reloaded pin below the minimum in effect as invalid, and apply a raised minimum immediately while the pin stays configured and a contradiction warning is logged once per distinct state
- [ ] T011 [P] Create `src/platform/SocAlytics.Platform.Infrastructure/Registry/UpdateFeed/FakeManagerReleaseSource.cs`: singleton with a mutation API for tests (add or replace releases, packages, hashes, SBOM and attestation outcomes, make the source fail), reachable through no HTTP route

### Verification cache

- [ ] T012 Create `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/ReleaseVerificationCache.cs` (uses the settings provider of T010): keyed by the release's immutable identity, verified results for `VerifiedCacheLifetime` and failures for `FailureCacheLifetime` (read from the settings provider at each evaluation), release list refreshed at most once per lifetime, single in-flight evaluation shared by concurrent requests, a verified release still within its lifetime kept after a source failure, a failed refresh with nothing verified cached reported as unavailable, lazy newest-first verification through the release source (the only place that triggers verification), handing the selector an already-verified list

### Registration

- [ ] T013 Register the foundational services in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (options bound to `Registry:UpdateFeed` with `ValidateOnStart`, settings provider, validator, cache, metrics) and in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` (`IManagerReleaseSource` selected by `Source`: `Fake`, otherwise a source that offers nothing), set `Registry:UpdateFeed:Source` to `Fake` in `src/platform/SocAlytics.Platform.Api/appsettings.Development.json`, and register the `SocAlytics.Platform.Registry` meter in `src/platform/SocAlytics.Platform.ServiceDefaults/ServiceDefaultsExtensions.cs` the way the analysis meter is

**Checkpoint**: The platform builds (`dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`) and starts with the fake source.

---

## Phase 3: User Story 1 - A Manager learns from its own stamp that a newer build exists (Priority: P1) MVP

**Goal**: The platform serves the offer and the Manager shows a notice-only "update available" when the offer is newer than its build.

**Independent Test**: With the fake source offering a newer version, a Manager of an older build shows "update available" with the offered version in its status and CLI `status`, and a direct authenticated request returns version, channel, package names, sizes, SHA-256 hashes, and release notes link.

### Platform operation

- [ ] T014 [US1] Create the pure `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/ReleaseOfferSelector.cs` per the selection rules of [data-model.md](data-model.md) and R2 of [research.md](research.md): offerable channel set (`stable` is `{stable}`, `preview` is `{stable, preview}`), over an already-verified list handed over by the `ReleaseVerificationCache` of T012 (which performs the lazy newest-first verification; the selector does no I/O), highest verified version at or above the minimum, exactly the pin when offerable, verified, and at or above the minimum (never another version), reason precedence for the unavailable answer
- [ ] T015 [US1] Create `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/ReadUpdateOfferHandler.cs` following `ReadQueueDepthHandler`: no `ManagerVersionGate`, a missing registration is unauthenticated and a registration that is not active is forbidden `manager-not-active`, an unavailable feed is `OperationFailure.Unavailable` with problem `update-feed-unavailable`, the offer carries no registration data, credentials, or other stamp configuration, and register the selector and handler in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`
- [ ] T016 [US1] Add `GET /api/v1/analyst-managers/self/update-offer` (`getAnalystManagerUpdateOffer`, policy `AnalystManager`, HTTPS filter, `Cache-Control: no-store`, tag "Analyst Manager self", responses `200`, `401`, `403`, `503`) to `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs`
- [ ] T017 [US1] Refresh the committed `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (local only, never in the verification environment) and confirm the diff only adds the new operation
- [ ] T018 [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/UpdateOfferTests.cs` with the harness of `QueueDepthTests.cs` and the fake source: offer content equals the verified release values (version, channel, packages, sizes, hashes, release notes link), the same offer for every Manager, all packages listed regardless of platform, nothing in the channel states `no-release-in-channel`, and the response validates against `update-offer.schema.json`

### Manager notice

- [ ] T019 [P] [US1] Create `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/UpdateOfferState.cs`: in-memory `OfferedVersion`, `Channel`, `ReleaseNotesUrl` of the last successful offer that names a release newer than the build (`ManagerVersionComparer`), cleared by a successful answer that does not, unchanged by failure, `503`, or unreadable body, read by nothing in admission, claims, attempts, or lifecycle
- [ ] T020 [P] [US1] Add `GetUpdateOfferAsync` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs` (same session and DPoP proof path, `503` as transient failure) and the offer DTOs to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformWorkContracts.cs`, treating an unreadable or unknown body as no offer (FR-025)
- [ ] T021 [US1] Add `PollUpdateOfferAsync` after `PollQueueDepthAsync` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/RegistrationStatusMonitor.cs`: asks only the stamp, replaces `UpdateOfferState` on success, leaves it unchanged on failure, and never changes cadence or backoff
- [ ] T022 [US1] Add `UpdateAvailableStatus` (`OfferedVersion`, `ManagerVersion`, channel) beside `UpdateRequiredStatus` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/ManagerStatusSnapshot.cs`, expose it through `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerControl.cs`, and register `UpdateOfferState` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerServiceCollectionExtensions.cs`
- [ ] T023 [US1] Add the optional `status.updateAvailable` object (`offeredVersion`, `managerVersion`, `channel`; additive) to `src/analysts/manager/contracts/local-control.schema.json`, emit it from `src/analysts/manager/SocAlytics.Analysts.Manager.Core/LocalControl/LocalControlServer.cs`, and print `"Update available: <version>"` in plain `status` and in `--json` from `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Cli/ManagerCli.cs`, distinct from the minimum notice
- [ ] T024 [US1] Show a separate "update available" line with the offered version in the status window (`src/analysts/manager/SocAlytics.Analysts.Manager/ViewModels/StatusWindowViewModel.cs`, `src/analysts/manager/SocAlytics.Analysts.Manager/Views/StatusWindow.axaml`) and as a status line in the tray menu (`src/analysts/manager/SocAlytics.Analysts.Manager/ViewModels/TrayMenuViewModel.cs`, `src/analysts/manager/SocAlytics.Analysts.Manager/Tray/TrayMenuBuilder.cs`), with no operating-system notification and no action that downloads or applies anything
- [ ] T025 [US1] Extend the Manager test doubles `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Support/FakePlatform.cs` and `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Support/FakeManagerControl.cs` with a controllable update offer (available, none, `503`, unreadable body)

### Tests for the Manager notice and end to end

- [ ] T026 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/UpdateOfferStateTests.cs` and `UpdateOfferPollingTests.cs` in the same folder (newer shows the notice, equal or older shows none and never a downgrade, a successful "none" clears it, failure, `503`, and unreadable body keep it and create none, no cadence change, the polling calls only the stamp's update-offer operation and no other source)
- [ ] T027 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/LocalControl/StatusUpdateAvailableTests.cs` (snapshot, local-control payload validated against `local-control.schema.json`, CLI plain and `--json`, headless profile)
- [ ] T028 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Ui/StatusWindowUpdateAvailableTests.cs` (status window line and tray menu line with the offered version, distinguishable from the minimum notice, both shown at once)
- [ ] T029 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/UpdateFeedEndToEndTests.cs`: a Manager of an older build against the platform with the fake source shows the notice within one check interval plus 10 seconds, a current build shows none (Linux engine)

**Checkpoint**: User Story 1 works on its own: `dotnet test` filters `UpdateOffer` on the platform Integration and Manager tests pass.

---

## Phase 4: User Story 2 - A stamp operator controls which release the fleet is offered (Priority: P1)

**Goal**: Channel and pin are validated at start, applied on reload without restart or re-registration, and drive the offer.

**Independent Test**: With the fake source holding releases in both channels, set the channel, set a pin, and clear the pin; each offer follows the setting after a reload.

- [ ] T030 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Host.Tests/UpdateFeedOptionsTests.cs` modelled on `ManagerVersionOptionsTests.cs`: defaults (`stable`, no pin, 15 minutes, 1 minute), an invalid channel, a malformed pin, and a non-positive or malformed cache lifetime each stop startup naming the option, a pin below the minimum names both options, an invalid reload keeps the last valid value and logs
- [ ] T031 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/UpdateFeedChannelPinTests.cs`: `stable` offers only stable releases, `preview` offers the highest version among preview and stable (a newer stable wins), a `preview` release is never offered on `stable`, a pin is offered exactly even when a newer release exists, a pin that matches nothing or is unverified offers nothing with `no-release-for-pin`, and channel and pin changes apply on reload within one check without restart or registration change

---

## Phase 5: User Story 3 - Only verified releases are offered (Priority: P1)

**Goal**: A release is offered only after its SBOM, attestation, and package hashes and sizes verified; refusals are counted and logged; the GitHub source never runs in Development and Test.

**Independent Test**: With the fake source holding a failing attestation, a missing SBOM, and a valid older release, the offer is the valid release (or nothing), both failures are counted with their reasons, and no test reaches GitHub.

### Verification and sources

- [ ] T032 [P] [US3] Create `src/platform/SocAlytics.Platform.Infrastructure/Registry/UpdateFeed/AttestationBundleVerifier.cs` per R5 of [research.md](research.md) using `System.Security.Cryptography` only: SBOM present, bundle per package digest, DSSE signature through a certificate chain to the configured trusted root, signing identity equal to the configured expected identity, in-toto subjects matching each package name and digest, stated size equal to the asset size, SBOM digest matching its subject when covered; without a configured trusted root or expected identity every release is `attestation-invalid` and nothing is requested from GitHub
- [ ] T033 [P] [US3] Create `src/platform/SocAlytics.Platform.Infrastructure/Registry/UpdateFeed/GitHubManagerReleaseSource.cs` that proxies the Manager's GitHub Releases through `HttpClient` (latest 30 releases, the prerelease flag as channel label, assets with name, size, digest, SBOM and bundle references) from the configured owner, repository, and API base address, with no production defaults
- [ ] T034 [US3] Record every refusal in `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/ReleaseVerificationCache.cs`: one structured warning log (version, channel, reason class, no credentials) and one `UpdateFeedMetrics` count by `reason` per evaluation (cache miss), never per request, no registration identifiers, no security audit event
- [ ] T035 [US3] Select the `GitHub` source in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` only when `Registry:UpdateFeed:Source` is `GitHub` (typed `HttpClient`, verifier registered), so `None` makes no outbound call, and require the `GitHub` subsection in `src/platform/SocAlytics.Platform.Application/Registry/UpdateFeed/UpdateFeedOptionsValidator.cs`

### Tests

- [ ] T036 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/UpdateFeedVerificationTests.cs`: missing SBOM, missing or failing attestation, hash mismatch, size mismatch, missing hash or size, and malformed version are never offered and each release actually evaluated and refused is counted once per evaluation by reason in `socalytics.registry.update_feed_refusals` and logged once per evaluation with version, channel, and reason class and no credentials (asserted on the structured log fields), an older failing release behind a verified newer one is not evaluated and not counted, the next newest verified release is offered (nothing under a pin), verified results are not re-verified within 15 minutes, a changed digest for the same version is verified anew, failures are retried after 1 minute, and a source failure answers `503` while a cached verified release keeps being offered
- [ ] T037 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/AttestationBundleVerifierTests.cs` with fixture bundles signed by a throwaway test CA and an explicit non-production identity (valid, wrong identity, untrusted root, tampered statement, subject mismatch, no configured roots)
- [ ] T038 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/GitHubManagerReleaseSourceTests.cs` against a stub `HttpMessageHandler` (channel label from the prerelease flag, at most 30 releases, an error response reported as a source failure); no test contacts GitHub

---

## Phase 6: User Story 4 - The offer never contradicts the minimum version (Priority: P2)

**Goal**: The offer never falls below the minimum and never changes the Manager's admission of work.

**Independent Test**: With a minimum of `1.2.0` and verified `1.1.0`, `1.2.0`, and `1.3.0`, the offer is never `1.1.0`; with only `1.1.0` nothing is offered.

- [ ] T039 [P] [US4] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/UpdateFeedMinimumVersionTests.cs`: no release below the minimum is offered, `no-release-satisfies-minimum` when the latest verified is below it, a reload that raises the minimum above the pin applies at once, offers nothing, and logs the contradiction until the pin changes, a Manager below the minimum still reads the offer, and the minimum-version refusals of other operations are unchanged
- [ ] T040 [P] [US4] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/UpdateOfferNoSideEffectTests.cs`: with an update available throughout, no package download, drain, restart, or change to claims, attempts, or registration occurs, a Manager below the minimum still admits no new work, the offer is no permission to claim work, and no operating-system notification is raised

---

## Phase 7: User Story 5 - Only an authenticated Manager reads the offer (Priority: P2)

**Goal**: Only an active, DPoP-authenticated Manager reads the offer.

**Independent Test**: Request without credentials, with a human session, with a revoked Manager's token, and with an active Manager's token; only the last succeeds.

- [ ] T041 [P] [US5] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/UpdateOfferAuthorizationTests.cs` with the machine-authentication harness: no token, human session, invalid or replayed DPoP proof, wrong audience or scope, pending, rejected, revoked, and unregistered registrations are refused as for other Manager operations and return no offer, an active Manager succeeds, the response carries no registration data

---

## Phase 8: Polish and Cross-Cutting Concerns

**Purpose**: Documentation that makes `docs/architecture/` and the READMEs true for the merged state (FR-026), then verification.

### Documentation

- [ ] T042 [P] Make `docs/architecture/analyst-manager.md` true for the merged state per the Architecture Updates section of [plan.md](plan.md): in Updates, remove the "This decision is not implemented yet." sentence and correct every statement that the feed, channels, pinning, and attestation checks do not exist (implemented as development evidence), describe the fake release source, the refusal metric, the Manager's notice-only "update available", and the DPoP-authenticated offer in Machine Authentication, and keep installers, package download, background download, draining, self-update, Manager-side signature verification, the publisher identity, the release workflow, production GitHub access, and production values explicitly unimplemented; re-read the whole file for other planned or future-tense wording this feature makes false
- [ ] T043 [P] Make `docs/architecture/platform-implementation.md` and `docs/architecture/analysts-models-and-hardware.md` true for the merged state (the feed, channels, pinning, and attestation verification are implemented; installers, Velopack, and the self-update spike stay planned), re-reading both for other affected statements
- [ ] T044 [P] Update `src/platform/README.md` with the operation, the `Registry:UpdateFeed` options and defaults (development cache lifetimes), the fake source, the refusal metric, and the correction that the update feed does not exist, keeping production values unresolved
- [ ] T045 [P] Update `src/analysts/manager/README.md` and `src/analysts/README.md` with the "update available" notice in the status window, tray menu, and CLI `status`, the additive `status.updateAvailable`, and the correction of the statements that updates, attestation, and the feed do not exist, keeping download, apply, and installers unimplemented
- [ ] T046 [P] Update the Current State paragraph of `AGENTS.md` to describe the update feed as development evidence (operation, options, fake source, notice) without claiming download, self-update, installers, or production readiness

### Verification

- [ ] T047 Run `node .github/scripts/check-markdown.mjs` and fix every finding in the changed Markdown
- [ ] T048 Run the validation of [quickstart.md](quickstart.md) (Host, Contracts, Integration, Manager, and EndToEnd filters) and the existing suites unchanged (`dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build`)

---

## Dependencies and Execution Order

- Phase 1 (contract) before everything that validates against it; T001 before T002 to T006, T004 after T001.
- Phase 2 blocks all stories: T007 to T009 first, then T010 and T011, then T012 (the cache uses the settings provider), then T013.
- US1 (MVP) depends on Phase 2; US2, US3, US4, and US5 depend on US1's endpoint (T014 to T016); US3's T034 and T035 edit files created in Phase 2, so they follow T013 and T015.
- US4 and US5 tests depend on US1 and US2 (pin and minimum behavior); US2, US4, and US5 are independent of each other and of US3 apart from shared test files being distinct.
- Phase 8 documentation follows the implementation it describes; T047 and T048 are last.

## Parallel Execution Examples

- Phase 1: T002 with T003; then T005 with T006.
- Phase 2: T007, T008, T009 together; then T010 with T011.
- US1 Manager: T019 with T020; then T026, T027, T028, and T029 together after T025.
- US3: T032 with T033; later T036, T037, T038 together.
- Polish: T042 to T046 together.

## Implementation Strategy

- **MVP**: Phases 1 and 2 and US1 (T001 to T029): a Manager learns of a newer verified build from the fake source and shows a notice only.
- **Incremental**: US2 (operator control), then US3 (real verification and the GitHub source), then US4 and US5 as verification of the minimum and authorization rules, then documentation and the quickstart run.
- Nothing downloads, stages, or applies a package, and no step contacts GitHub.
