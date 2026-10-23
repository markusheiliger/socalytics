# Data Model: Analyst Manager Update Feed

No database tables, migrations, or persisted entities are added. Everything below is configuration or in-memory state.

## Options: `Registry:UpdateFeed` (platform)

| Field | Type | Default | Validation |
| --- | --- | --- | --- |
| `Channel` | `stable` or `preview` | `stable` | Exactly one of the two values; otherwise start fails naming `Registry:UpdateFeed:Channel`. |
| `PinnedVersion` | `MAJOR.MINOR.PATCH` or empty | empty (no pin) | `ManagerVersion.TryParse`; start fails naming the option; below `Registry:ManagerVersion:Minimum` fails naming both options. |
| `VerifiedCacheLifetime` | duration | 15 minutes (development value) | Positive; otherwise start fails naming the option. |
| `FailureCacheLifetime` | duration | 1 minute (development value) | Positive; otherwise start fails naming the option. |
| `Source` | `None`, `Fake`, `GitHub` | `None` (Development and Test: `Fake`) | Known value; `GitHub` requires its subsection. |
| `GitHub` | owner, repository, API base address, trusted root, expected identity | empty | Used only by the `GitHub` source; production values stay a deployment decision. |

Reload: every option is applied on configuration reload; an invalid reloaded value keeps the last valid value and is
logged (provider pattern of `MinimumManagerVersionProvider`).

## In-memory entities (platform)

- **ManagerRelease** (from the release source): `Version` (text as labelled), `Channel` (`stable` or `preview`),
  `ReleaseNotesUrl`, `Packages[]` (`Name`, `SizeBytes`, `Sha256`), `SbomReference`, `AttestationReferences`, and an
  immutable identity (source release id plus asset digests) used as the cache key.
- **ReleaseVerificationResult**: `Verified` with the verified values, or `Refused` with one reason class:
  `sbom-missing`, `attestation-missing`, `attestation-invalid`, `subject-mismatch`, `package-size-mismatch`,
  `package-unverifiable`, `version-malformed`.
- **VerifiedReleaseCacheEntry**: result, expiry (verified or failure lifetime), keyed by release identity.
- **ReleaseOffer** (response): `Available` with `Version`, `Channel`, `ReleaseNotesUrl`, `Packages[]`; or unavailable
  with a reason: `no-release-in-channel`, `no-verified-release`, `no-release-satisfies-minimum`,
  `no-release-for-pin`. Plus `observedAt` (the time of the evaluation the answer is based on) and the stamp's
  effective `channel`. Never stored per registration; carries no registration data, credentials, or other stamp
  configuration.

## Selection rules

1. Offerable channel set: `stable` → `{stable}`; `preview` → `{stable, preview}`.
2. Verification refuses a version that is not `MAJOR.MINOR.PATCH` (`version-malformed`, counted and logged), so the selector never sees one; it compares numerically per part (`ManagerVersion`).
3. Unpinned: the highest verified offerable version at or above the minimum.
4. Pinned: exactly the pin when offerable, verified, and at or above the minimum; else nothing (never another version).
5. A refusal is counted once per evaluation (a cache miss), not per request.

## Manager state (in memory only)

- **UpdateOfferState**: `OfferedVersion`, `Channel`, `ReleaseNotesUrl` of the last successful offer that named a release
  newer than the Manager's build (`ManagerVersionComparer`); cleared by a successful answer that does not. Unchanged by
  a failed check, a `503`, or an unreadable body. Lost on restart (the next check restores it).
- **UpdateAvailableStatus** (snapshot): `OfferedVersion`, `ManagerVersion`, shown beside, and distinct from,
  `UpdateRequiredStatus` (minimum notice). Exposed as optional `updateAvailable` in the local-control status payload.

No state transition in claims, attempts, admission, lifecycle, or registration reads `UpdateOfferState`.
