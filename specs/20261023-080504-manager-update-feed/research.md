# Research: Analyst Manager Update Feed

## R1. Operation shape and status codes

- **Decision**: `GET /api/v1/analyst-managers/self/update-offer`, operation id `getAnalystManagerUpdateOffer`, policy
  `AuthorizationPolicyNames.AnalystManager`, HTTPS filter, `Cache-Control: no-store`, tag "Analyst Manager self". A
  success is always `200` with an `available` flag: `available: true` carries the release, `available: false` carries
  one `reason` of `no-release-in-channel`, `no-verified-release`, `no-release-satisfies-minimum`,
  `no-release-for-pin`. An unreachable or failing release source (nothing verified and cached) answers `503` with the
  problem `update-feed-unavailable` through `OperationFailure.Unavailable`, as `queue-depth` does with
  `queue-unavailable`. Registrations that are not active follow `ReadQueueDepthHandler`: unauthenticated when missing,
  forbidden `manager-not-active` otherwise. The handler does not use `ManagerVersionGate`.
- **Rationale**: mirrors the existing Manager self reads (same authentication, error vocabulary, and client code path),
  so FR-003 (no version refusal, same authorization) follows from reuse. Distinguishing "none available" (200) from
  "temporarily unavailable" (503) lets the Manager keep its notice on a failure (FR-024) and clear it on a clean
  "none".
- **Alternatives**: a POST with the Manager's version (rejected: the offer is the same for the whole stamp, FR-004, and
  the Manager compares itself); returning 200 with `unavailable` (rejected: the shared client already maps 503 to a
  transient failure); a public unauthenticated feed (rejected: FR-003).

## R2. Offer selection

- **Decision**: `ReleaseVerificationCache` performs the lazy newest-first verification through the release source and
  hands `ReleaseOfferSelector` an already-verified list; the selector is a pure function over that list, the channel,
  the pin, and the minimum:
  1. Offerable set: `stable` channel → releases labelled `stable`; `preview` → `stable` and `preview`. A version that is not
     `MAJOR.MINOR.PATCH` (`ManagerVersion.TryParse`) is refused at verification (`version-malformed`), so the selector
     never sees it.
  2. Without a pin: highest version at or above the minimum among verified offerable releases.
  3. With a pin: exactly that release if offerable, verified, and not below the minimum; never a different version.
  4. Reason precedence for `available: false`: pin set and no offerable release matches → `no-release-for-pin` (the
     release exists but failed verification also yields this class, with the refusal already counted); minimum raised
     above the pin or all verified releases below it → `no-release-satisfies-minimum` (and a warning log for a
     contradicting pin until the pin changes); no offerable release at all → `no-release-in-channel`; offerable
     releases exist but none verified → `no-verified-release`.
- **Rationale**: matches FR-009 to FR-012 and the clarifications; keeping it pure makes SC-003/SC-004 testable without
  I/O. Verification is evaluated lazily newest-first by the cache so older releases are only verified when newer ones fail; only releases actually evaluated and refused are counted.
- **Alternatives**: verifying every release each time (rejected: cost and noisy refusal counts, SC-002 counts "once per
  evaluation"); offering the next lower version for a pin (rejected: FR-009).

## R3. Settings provider and validation

- **Decision**: options `Registry:UpdateFeed` with `Channel` (`stable` default), `PinnedVersion` (empty default),
  `VerifiedCacheLifetime` (15 minutes), `FailureCacheLifetime` (1 minute), `Source` (`None`, `Fake`, or `GitHub`), and a
  `GitHub` subsection (repository owner and name, API base address, trusted root and expected identity references; all
  empty by default). `UpdateFeedSettingsProvider` follows `MinimumManagerVersionProvider`: validated at start with
  `Validate` (`ValidateOnStart`), read through `IOptionsMonitor`, last valid value kept after an invalid reload with one
  logged warning per invalid state. A pin below the minimum is validated at start together with
  `ManagerVersionOptions` (message names both options); a reload that moves the pin below the minimum in effect
  keeps the last valid pin and logs; a reload that raises the minimum above the pin applies the minimum and logs the
  contradiction at warning level (rate-limited to once per distinct state).
- **Rationale**: FR-006 to FR-008, FR-011, FR-012, FR-017 and the existing convention for
  `Registry:ManagerVersion:Minimum`. Cross-option validation lives in a startup validator that reads both option
  objects.
- **Alternatives**: a database setting (rejected: the spec requires deployment options applied on reload).

## R4. Release source abstraction

- **Decision**: `IManagerReleaseSource` returns `ManagerRelease` candidates (version text, channel label, release notes
  URL, packages with name, size, SHA-256 as the source states it, SBOM reference, attestation reference or bundle) and
  a verification service that returns `ReleaseVerificationResult` (verified or a reason class). Selection of the
  implementation: `Fake` in Development and Test (the `appsettings.Development.json` value and the test host default),
  `GitHub` only when configured, `None` otherwise, so a stamp without configuration offers nothing
  (`no-release-in-channel`) and makes no outbound call. The fake source is a singleton with a mutation API used by
  tests (add or replace releases, set an SBOM or attestation outcome, make the source fail) and is not reachable
  through any HTTP route.
- **Rationale**: FR-015, FR-020; satisfies "no outbound access in Development and Test" and keeps production values
  undecided.
- **Alternatives**: calling GitHub from tests with a stub server (rejected: SC-007 forbids contact; the fake plus
  fixture bundles is enough).

## R5. Verification of SBOM and attestation

- **Decision**: a release is verified when all of the following hold: it lists at least one package and every package
  has a name, a size, and a lowercase hex SHA-256; the release has an SBOM asset (reason `sbom-missing`); a build
  provenance attestation bundle exists for every package digest (`attestation-missing`); the bundle's DSSE signature
  validates through a certificate chain to a configured trusted root and the signing identity matches the configured
  expected identity, using `System.Security.Cryptography` only (`attestation-invalid`); the in-toto statement subjects
  contain each package's name and digest (`subject-mismatch`; `package-size-mismatch` when the stated size differs from
  the asset size the source reports); and the SBOM digest matches the SBOM subject when the attestation covers it. Any
  reason class above, plus `package-unverifiable` (missing hash or size) and `version-malformed`, is a refusal. Without
  a configured trusted root or expected identity, the GitHub source treats every release as `attestation-invalid` and
  does not call GitHub for bundles.
- **Rationale**: FR-016, FR-019; the architecture states the stamp verifies the attestation before offering. The
  trusted roots and identities are production deployment decisions the spec leaves open (FR-020), so they are options
  with no production defaults. Tests sign bundles with a throwaway test CA and an explicit non-production identity.
- **Alternatives**: adopting a Sigstore client package (rejected for this feature: new dependency and trust decision;
  can be adopted by a later spec); shelling out to `gh attestation verify` (rejected: tooling dependency inside the API
  image); trusting GitHub's attestation API without verifying the bundle (rejected: not verification).

## R6. Cache

- **Decision**: `ReleaseVerificationCache` keyed by the release's immutable identity (version plus the digest of its
  SBOM and the package digest list, or the GitHub release id and asset digests) holds verified results for the
  verified lifetime and failures for the failure lifetime; the release list itself is refreshed at most once per
  failure lifetime on error and once per verified lifetime otherwise; concurrent requests share one in-flight
  evaluation. While the source is failing, a verified release still within its lifetime keeps being offered (FR-018).
  Lifetimes are read from the settings provider at each evaluation, so a reload applies to the next evaluation. A
  changed channel or pin re-runs selection over the cached verification results without touching the source, so the
  offer follows a reload within one check (SC-003).
- **Rationale**: FR-017, FR-018.
- **Alternatives**: `IMemoryCache` with absolute expiry only (rejected: needs single-flight and per-identity keys);
  persisting verification (rejected: no storage in scope).

## R7. Metrics and logs

- **Decision**: counter `socalytics.registry.update_feed_refusals` in a new `UpdateFeedMetrics` (meter
  `SocAlytics.Platform.Registry`, tag `reason` only), incremented once per refusal per evaluation, not per request, and
  structured warning logs with version, channel, and reason class; no credentials, registration identifiers, or
  release content as dimensions; no security audit event (FR-019). The meter is registered with the ServiceDefaults
  OpenTelemetry configuration the same way `AnalysisMetrics` is.
- **Rationale**: follows `socalytics.analysis.manager_version_refusals`.

## R8. Manager integration

- **Decision**: after `PollQueueDepthAsync`, `RegistrationStatusMonitor` calls `PollUpdateOfferAsync`, which requests
  the offer through `PlatformClient.GetUpdateOfferAsync` using the same session and DPoP proof path. A success replaces
  the in-memory `UpdateOfferState` (the offered version when `available` and newer than the build per
  `ManagerVersionComparer`, otherwise cleared); a transient failure, `503`, or an unreadable body leaves the state
  unchanged and never changes the check cadence or the backoff (FR-024, FR-025); an inactive answer is handled by the
  existing status path. The Manager's status snapshot gains an optional `updateAvailable` (offered version only; the
  channel and notes link are shown in `--json` but no download is possible), distinct from `updateRequired`. The
  tray status window and menu get a separate line; the CLI plain text prints "Update available: " followed by the version. The state
  is memory-only and nothing in admission, claims, attempts, or lifecycle reads it.
- **Rationale**: FR-021 to FR-025; the polling slot already exists for guidance-only reads.
- **Alternatives**: polling on its own timer (rejected: spec fixes the existing interval); carrying the offer in the
  status report response (rejected: the spec defines a separate operation and the offer is not report data).

## R9. Contracts and compatibility

- **Decision**: new schema `contracts/analyst-manager/update-offer/v1/update-offer.schema.json` (`1.0.0`, JSON Schema
  2020-12, `additionalProperties: false`) with valid and invalid examples, catalogued in `contracts/README.md`, covered
  by `ContractCatalog`/`ContractIndexTests` and a `UpdateOfferContractTests` in both the platform Contracts.Tests and
  the Manager tests, like `QueueDepthContractTests`. The Manager local-control schema gains an optional
  `status.updateAvailable` object (additive, so compatible); the OpenAPI snapshot is refreshed with the documented
  snapshot test only.
- **Rationale**: FR-005 and the contracts convention.

## R10. Documentation scope

- **Decision**: the documentation task updates the platform README (options, fake source, metric, operation), Manager
  and Analysts READMEs, `contracts/README.md`, `docs/architecture/` (see the Architecture Updates section), and
  `AGENTS.md`, correcting "feed, channels, pinning, attestation checks do not exist" and keeping installers, download,
  apply, Manager-side signature verification, publisher identity, release workflow, and production values unimplemented
  (FR-026).
