# Feature Specification: Analyst Manager Update Feed

**Feature Branch**: `20261023-080504-manager-update-feed`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "The platform serves the update feed that Analyst Managers use to learn about newer builds, as the architecture's Analyst Manager 'Updates' section decides. User value: a stamp operator controls which Manager release the stamp's fleet is offered, and a Manager can find out from its own stamp, with verifiable hashes, whether a newer build exists, instead of an operator installing builds by hand. Scope: a platform operation, reachable only with the Manager's DPoP machine authentication, that returns the release offer for the Manager (version, package names, sizes, SHA-256 hashes, channel, release notes link); stamp deployment options for the channel (`stable` or `preview`) and an optional pinned version, validated at start and applied on reload like `Registry:ManagerVersion:Minimum`; a release source abstraction that proxies the Manager's GitHub Releases and offers a release only after its SBOM and GitHub artifact attestation (build provenance) verified, with a fake release source for Development and Test and tests that never reach GitHub; refusal and metrics for releases that fail verification; the offer never lowers or contradicts the stamp's minimum version; the Manager's status window, tray menu, and CLI `status` show 'update available' with the offered version (notice only, the Manager keeps its work and applies nothing). Out of scope: package download, background download, draining and applying an update (self-update), installers, Velopack and signing spikes, the publisher identity, the release workflow that publishes SBOMs and attestations, the production outbound GitHub access and production channel values, and anything the architecture marks as an open promotion item. Depends on the implemented specs Analyst Manager Registration, Analyst Manager Status Report and Fleet Read API, and Analyst Manager Minimum Version. Implements docs/architecture/analyst-manager.md#updates and updates its statement that the feed does not exist."

## Clarifications

### Session 2026-10-23

- Q: When the stamp's minimum Manager version is raised on reload above the version pinned for the update feed, what should the feed offer and which setting stays in effect? → A: The new minimum applies immediately as it does today; the pin stays configured but cannot be offered, so the feed offers nothing (reason: no release satisfies the minimum) and logs the contradiction until the pin is changed.
- Q: Should the offer be limited to the Manager's own operating system and CPU architecture, or is one platform-independent offer shown to every Manager? → A: One offer for the whole stamp listing all packages of the release; every Manager with an older build shows the notice whether or not a package exists for its platform (as specified now).
- Q: Should a stamp on the `preview` channel also be offered a newer `stable` release when it is newer than the latest preview release? → A: Yes. The preview channel offers the highest verified version among preview and stable releases; the stable channel offers only stable releases.
- Q: How long should a verified release and a failed verification stay cached before the stamp checks the release source again, and may the operator change these lifetimes? → A: Fixed development values: verified releases cached for 15 minutes and failures for 1 minute, as platform options validated at start and applied on reload like the other feed options.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A Manager learns from its own stamp that a newer build exists (Priority: P1)

A host operator runs a registered Analyst Manager. The stamp's operator has configured the stamp to offer releases of
one channel (`preview` also includes stable releases). At its periodic check, the Manager asks its own stamp (and nothing else) for the release offer. If the
stamp offers a release newer than the Manager's own build, the Manager shows "update available" with the offered
version in its tray status window, as a status line in the tray menu, and in the local-control CLI `status` output
(plain text and `--json`). The Manager keeps its registration, status reports, and work exactly as before and applies
nothing; the operator installs the newer build by hand, as today. The offer carries everything needed to verify the
build later: version, channel, package names, sizes, SHA-256 hashes, and a release notes link.

**Why this priority**: This is the feed itself and the only part that reaches the host operator. Without it the
operator cannot learn from the stamp that a newer build exists.

**Independent Test**: With the fake release source offering a newer version, a Manager of an older build shows
"update available" with the offered version in its status and CLI `status`, and a direct authenticated request for the
offer returns the version, channel, package names, sizes, SHA-256 hashes, and release notes link.

**Acceptance Scenarios**:

1. **Given** an active Manager and a verified release newer than its build in the stamp's channel, **When** the
   Manager checks for updates, **Then** it shows "update available" with the offered version in the tray status
   window, the tray menu, and the CLI `status` output, and its work, registration, and status reports continue
   unchanged.
2. **Given** an active Manager whose build equals or is newer than the offered release, **When** it checks for
   updates, **Then** it shows no update notice.
3. **Given** an offered release, **When** the offer is read, **Then** it names the version, channel, every package with
   its name, size, and SHA-256 hash, and a release notes link, and these values are exactly the verified values of
   that release.
4. **Given** the Manager is below the stamp's minimum version, **When** an update is also available, **Then** the
   minimum-version notice and the "update available" notice are distinguishable, the offered version satisfies the
   minimum, and the offer changes nothing about the Manager's admission of work.
5. **Given** an update stays available for days, **When** the Manager keeps checking, **Then** it never downloads a
   package, drains Analysts, restarts, or installs anything because of the offer.
6. **Given** a headless Manager, **When** its operator runs the CLI `status` command (plain and `--json`), **Then**
   the output names the available update and its version.

---

### User Story 2 - A stamp operator controls which release the fleet is offered (Priority: P1)

A stamp operator chooses the release channel (`stable` or `preview`) and may pin one version in the stamp's deployment
options. By default the stamp offers the latest verified release of its channel. When a version is pinned, the stamp
offers exactly that version, if it is verified, even when a newer release exists. The operator changes channel or pin
by changing deployment options; the change takes effect on configuration reload without restarting the platform and
without re-registering any Manager. An invalid value stops the platform from starting, and an invalid reloaded value
leaves the last valid value in effect and is logged.

**Why this priority**: The operator control is the user value of the feature ("which Manager release the stamp's fleet
is offered") and equally critical to the feed.

**Independent Test**: With the fake release source holding releases in both channels, set the channel, set a pin, and
clear the pin; each Manager's offer follows the setting after a reload, without restart or re-registration.

**Acceptance Scenarios**:

1. **Given** no channel is configured, **When** a Manager asks for its offer, **Then** the stamp uses the `stable`
   channel.
2. **Given** the `preview` channel, **When** a Manager asks for its offer, **Then** the offer is the highest verified
   version among preview and stable releases, so a stable release newer than the latest preview release is offered;
   **Given** the `stable` channel, **Then** only stable releases are offered.
3. **Given** a pinned version that is a verified release offerable on the channel, **When** a Manager asks for its offer,
   **Then** the offer is that version, even if a newer release exists.
4. **Given** a pinned version that no release offerable on the channel matches or that has not verified, **When** a Manager asks
   for its offer, **Then** the stamp offers nothing, states the reason class, and never offers a different version in
   its place.
5. **Given** a channel value other than `stable` or `preview`, or a pin that is not a well-formed version, **When**
   the platform starts, **Then** it refuses to start with a message that names the option.
6. **Given** a running platform, **When** the operator reloads configuration with an invalid channel or pin, **Then**
   the last valid values stay in effect and the problem is logged.
7. **Given** a configured pin below the stamp's minimum version at start, **When** the platform starts, **Then** it refuses to
   start with a message that names both options (FR-011).

---

### User Story 3 - Only verified releases are offered (Priority: P1)

The stamp offers a release only after it verified the release's SBOM and its GitHub artifact attestation (build
provenance) against the release's packages. A release whose SBOM is missing, whose attestation is missing or does not
verify, or whose package hashes do not match the attested subjects is never offered; the stamp records the refusal in
a metric and in logs, and keeps offering the best remaining verified release or nothing. The Manager does not rely on
the proxy for correctness, because it verifies package hashes itself later (outside this feature), but the stamp does
not hand it a release it could not verify.

**Why this priority**: The architecture states the stamp verifies the attestation before it offers a release. An
unverified offer would defeat the purpose of the feed.

**Independent Test**: With the fake release source holding one release with a failing attestation, one with a missing
SBOM, and one valid older release, a Manager's offer is the valid release (or nothing), the two failures are counted
with their reasons, and no test reaches GitHub.

**Acceptance Scenarios**:

1. **Given** a latest release whose attestation does not verify, **When** a Manager asks for its offer, **Then** that
   release is not offered, the refusal is counted and logged with its reason class, and the next newest verified
   release of the channel (not below the minimum) is offered instead, unless a pin applies (then nothing is offered).
2. **Given** a release without an SBOM, **When** the stamp evaluates it, **Then** it is refused with the reason class
   "SBOM missing".
3. **Given** a release whose package hash or size differs from the attested subject, **When** the stamp evaluates it,
   **Then** it is refused with a reason class that names the mismatch and is never offered.
4. **Given** a release that verified, **When** it is evaluated again within its cache lifetime, **Then** the stamp
   does not repeat the verification, and a changed release (a different digest for the same version) is verified anew.
5. **Given** the release source cannot be reached or answers with an error, **When** a Manager asks for its offer,
   **Then** the stamp answers that the feed is temporarily unavailable, offers no unverified or unknown release, and
   the Manager keeps working without creating or removing a notice.
6. **Given** the Development and Test configuration, **When** the platform runs, **Then** a fake release source with
   controllable releases and verification outcomes serves the feed and no test or local run contacts GitHub.

---

### User Story 4 - The offer never contradicts the minimum version (Priority: P2)

The stamp's minimum Manager version stays authoritative. The stamp never offers a release below its minimum, never
presents an offer as a reason to relax the minimum, and a Manager does not treat an available update as permission to
take work it would otherwise refuse.

**Why this priority**: The two rules come from the same stamp and must be consistent, but each is already enforced
independently.

**Independent Test**: With a minimum of `1.2.0` and releases `1.1.0`, `1.2.0`, and `1.3.0` verified in the channel,
the offer is never `1.1.0`; with a pin of `1.1.0`, the platform refuses to start, and with only `1.1.0` available,
nothing is offered.

**Acceptance Scenarios**:

1. **Given** a minimum and a channel whose latest verified release is below it, **When** a Manager asks for its offer,
   **Then** nothing is offered and the stamp states that no release satisfies the minimum.
2. **Given** a minimum and several verified releases, **When** the latest satisfies it, **Then** the latest is
   offered; no release below the minimum is ever offered.
3. **Given** a Manager below the minimum, **When** the offer is available, **Then** the Manager's admission of work is
   unchanged by the offer: it still admits no new work until it is at or above the minimum.
4. **Given** the minimum is raised above the pinned version on reload, **When** the platform applies the reloaded
   configuration, **Then** the new minimum applies immediately as today, the pin stays configured but cannot be
   offered, the offer states that no release satisfies the minimum, and the platform logs the contradiction until the
   pin is changed.

---

### User Story 5 - Only an authenticated Manager reads the offer (Priority: P2)

The offer is available only to an active, authenticated Analyst Manager of the stamp, proven by the Manager's DPoP
machine authentication. Unauthenticated callers, human sessions, Managers whose registration is not active, and tokens
of other kinds cannot read it. The offer is for the stamp's fleet as a whole, not per registration, and reveals no
registration data.

**Why this priority**: The feed must not become a public side channel to the stamp's configuration, but this follows
the existing machine-authentication rules.

**Independent Test**: Request the offer without credentials, with a human session, with a revoked Manager's token, and
with an active Manager's DPoP-bound token; only the last succeeds.

**Acceptance Scenarios**:

1. **Given** an active Manager with a valid DPoP-bound token and proof, **When** it reads the offer, **Then** the
   platform answers with the offer.
2. **Given** a request without a token, with a human session, with an invalid or replayed DPoP proof, with the wrong
   audience or scope, or from a registration that is pending, rejected, revoked, or unregistered, **When** it reads the
   offer, **Then** the platform refuses as it does for other Manager operations and returns no offer.
3. **Given** a Manager below the stamp's minimum version, **When** it reads the offer, **Then** the platform does not
   refuse because of the version, so the Manager can learn which release satisfies the minimum.

---

### Edge Cases

- No release exists yet in the channel (empty or new release source): the offer states that none is available; the
  Manager shows no notice.
- The offered version is not newer than the Manager's build (equal, a pinned older version, or a Manager built ahead of
  the channel): no update notice, and the Manager never suggests a downgrade.
- The channel switches from `preview` to `stable` while a Manager runs a preview build newer than the latest stable:
  no notice (never a downgrade).
- A release belongs to exactly one channel by its own label; a `preview` release is ignored on the `stable` channel,
  while the `preview` channel considers both preview and stable releases.
- A pin changes while Managers show a notice: the next successful check replaces or removes the notice; no
  re-registration.
- A release whose packages are listed without a hash or a size is refused as unverifiable and never offered.
- A verification failure is cached for 1 minute only, so a repaired release is offered again without restarting the
  platform; a verified release is cached for 15 minutes.
- A Manager that predates this feature never asks for the offer and is unaffected.
- The release notes link is supplied by the release; the stamp serves it as data only and never fetches it.

## Requirements *(mandatory)*

### Functional Requirements

#### Offer operation

- **FR-001**: The platform MUST provide one operation, reachable only with the Analyst Manager's DPoP machine
  authentication, that returns the release offer for the Manager: either the offered release or a statement that none
  is available together with the reason class (nothing in the channel, nothing verified, nothing satisfies the
  minimum, or no release for the pin); a feed that is temporarily unavailable is signalled as a `503` problem
  `update-feed-unavailable`, not as a statement.
- **FR-002**: An offered release MUST carry its version, its channel, its release notes link, and, for every package,
  its name, size in bytes, and SHA-256 hash, and these MUST be the verified values of that release.
- **FR-003**: The operation MUST be authorized exactly like the other Manager machine operations (valid DPoP proof,
  request binding, replay protection, token audience and scope, stamp binding, and an active registration) and MUST
  NOT be usable by human sessions, administrative APIs, or tokens of other audiences; it MUST NOT refuse a Manager
  because of its version relative to the stamp's minimum.
- **FR-004**: The offer MUST be the same for every Manager of the stamp, lists all packages of the release without
  filtering by the Manager's operating system or CPU architecture, and every Manager with an older build shows the
  notice whether or not a package exists for its platform; it MUST NOT contain registration data,
  credentials, or other stamp configuration, and the operation MUST NOT download, store, or serve any package content.
- **FR-005**: The operation and its response MUST be described by a machine-readable contract that the platform and
  the Manager tests share, with golden fixtures, as for the other Manager operations.

#### Channel, pin, and minimum

- **FR-006**: The stamp's deployment options MUST allow choosing the release channel, `stable` or `preview`, defaulting
  to `stable`, and an optional pinned version, empty meaning no pin.
- **FR-007**: The platform MUST validate both options (and the cache lifetimes of FR-017) when it starts and refuse to start when the channel is neither
  `stable` nor `preview` or the pin is not a well-formed version of the form the Manager reports, with a message that
  names the option.
- **FR-008**: A changed channel or pin MUST take effect on configuration reload without restarting the platform and
  without any registration change; a reloaded value MUST pass the same validation, and an invalid reloaded value MUST
  leave the last valid value in effect and be logged, like `Registry:ManagerVersion:Minimum`.
- **FR-009**: Without a pin, the stamp MUST offer the highest verified version among the releases offerable on its
  channel that satisfies the minimum; with a pin, it MUST offer exactly the pinned version when that release is
  offerable on the channel and verified, and otherwise offer nothing rather than another version.
- **FR-010**: A release belongs to the channel its own label states. The `stable` channel MUST offer only `stable`
  releases; the `preview` channel MUST offer the highest verified version among `preview` and `stable` releases. A
  `preview` release MUST NOT be offered on the `stable` channel.
- **FR-011**: The stamp MUST NOT offer a release below the stamp's minimum Manager version, and a configured pin below
  the minimum MUST stop the platform from starting with a message that names both options.
- **FR-012**: A reload that raises the minimum above the configured pin MUST apply the new minimum immediately, as
  for `Registry:ManagerVersion:Minimum` today; the pin stays configured but MUST NOT be offered, the offer MUST state
  that no release satisfies the minimum, and the contradiction MUST be logged until the pin is changed. A reload that
  changes the pin to a version below the minimum in effect after that reload MUST be treated like an invalid reload:
  the last valid pin stays in effect and the problem is logged.
- **FR-013**: The offer MUST NOT change, lower, relax, or otherwise contradict the minimum version: it MUST NOT alter
  which operations the platform refuses for the minimum, and a Manager MUST NOT treat an available update as
  permission to claim work.
- **FR-014**: The Manager MUST treat an offered version as an available update only when it is newer than the build
  version the Manager itself reports; an equal or older offered version is no update and never a downgrade.

#### Release source and verification

- **FR-015**: The platform MUST obtain releases through a release source abstraction. The production implementation
  proxies the Manager's GitHub Releases; a fake release source with controllable releases, packages, hashes, SBOMs,
  attestations, and verification outcomes MUST serve Development and Test, and no automated test or local run may
  contact GitHub.
- **FR-016**: The stamp MUST offer a release only after its SBOM and its GitHub artifact attestation (build
  provenance) verified, and after each offered package's SHA-256 hash and size matched the attested subject; a release
  that is missing any of them or fails any check MUST NOT be offered.
- **FR-017**: A verified release MUST be cached so that Managers' periodic checks do not repeat the verification or
  reach the release source for every request; the cache MUST be keyed by the release's immutable identity so that a
  changed release is verified anew, and a failure MUST be cached only briefly. A verified release stays cached for
  15 minutes and a failed verification for 1 minute (development values); both lifetimes are platform options
  validated at start (a non-positive or malformed value stops the platform with a message that names the option) and
  applied on reload like the other feed options, with the same last-valid-value rule for an invalid reload.
- **FR-018**: When the release source is unreachable or errors, the platform MUST answer that the feed is temporarily
  unavailable, MUST NOT offer an unverified release, MUST keep serving an already verified release that is still
  within its cache lifetime, and MUST NOT affect any other platform operation.
- **FR-019**: Every release refused for failed verification MUST be recorded in structured platform logs with the
  release version, channel, and reason class, without credentials, and MUST be counted in a platform metric by reason
  class, without registration identifiers or release content as metric dimensions; refusals are not security audit
  events.
- **FR-020**: The platform MUST NOT require outbound access to GitHub in Development and Test; the production outbound
  access, the identities the attestation is verified against in production, and production channel values remain
  deployment decisions that this feature does not make.

#### Manager notice

- **FR-021**: At its periodic check, an active Manager MUST ask its own stamp for the offer, MUST ask no other source,
  and MUST show "update available" with the offered version in its tray status window, as a status line in the tray
  menu, and in the CLI `status` output (plain text and `--json`) when FR-014 finds the offer newer than its build.
- **FR-022**: The notice MUST be a notice only: the Manager MUST keep its registration, status reports, work, and
  running attempts unchanged, MUST NOT download, stage, verify, install, drain for, or apply any package, and MUST NOT
  raise an operating-system notification.
- **FR-023**: The "update available" notice MUST be distinguishable from the minimum-version update notice, and both
  MAY be shown at once.
- **FR-024**: The notice MUST disappear at the Manager's next successful check once the offer no longer names a newer
  release (the Manager was updated by hand, the pin changed, or nothing is offered). A failed check or a "temporarily
  unavailable" answer MUST NOT remove a notice already shown nor create one, and MUST NOT affect the Manager's other
  behavior.
- **FR-025**: The Manager MUST treat an offer it cannot read or understand as no offer and keep working.

#### Documentation

- **FR-026**: The platform, Analyst Manager, and architecture documentation MUST describe the channel and pin options
  and their defaults, the cache lifetime options (development values), the verification the stamp performs, the fake release source, the Manager's notice, and the
  refusal metric, MUST correct the statements that the feed, channels, pinning, and attestation checks do not exist,
  and MUST keep as unimplemented the installers, package download, background download, draining and applying an
  update, signature verification by the Manager, the publisher identity, the release workflow, and production values.

### Key Entities

- **Release Offer**: What the stamp tells a Manager about a release: version, channel, release notes link, and the
  packages with name, size, and SHA-256 hash; or the statement that none is available with its reason class. It is
  not stored per registration.
- **Release**: A published build of the Analyst Manager in the release source with its channel label, packages, SBOM,
  and attestation. Only verified releases can be offered.
- **Release Source**: The abstraction through which the stamp finds releases and their verification material; GitHub
  Releases in production, a fake source in Development and Test.
- **Update Channel Setting**: The stamp's deployment option naming `stable` or `preview`; `preview` also offers
  stable releases.
- **Pinned Version Setting**: The stamp's optional deployment option naming the one version to offer.
- **Update Notice ("update available")**: The Manager's local indication, alongside its operating state, that its
  stamp offers a newer verified build, with the offered version.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests with the fake release source, a Manager shows "update available" with the offered
  version in its status window, tray menu, and CLI `status` within one check interval plus 10 seconds in 100% of
  cases where a newer verified release is offered, and in 0% of cases where none is.
- **SC-002**: In automated tests, 100% of releases with a missing SBOM, a missing or failing attestation, or a package
  hash or size mismatch are never offered, and every release actually evaluated and refused is counted once per evaluation in the
  refusal metric with its reason class (an older release behind a verified newer one is not evaluated).
- **SC-003**: In automated tests, a changed channel or pin changes the offer within one check interval plus 10 seconds
  of the configuration reload, without platform restart or any registration change, in 100% of cases.
- **SC-004**: In automated tests, 0 offered releases are below the stamp's minimum version, and a pin below the
  minimum stops the platform from starting in 100% of cases.
- **SC-005**: In automated tests, 100% of requests without a valid DPoP-authenticated active Manager registration are
  refused and none returns an offer.
- **SC-006**: In automated tests, with an update available for the full test duration, a Manager performs 0 package
  downloads, drains, restarts, or changes to its claims, attempts, and registration because of the offer.
- **SC-007**: No automated test and no Development or Test run contacts GitHub.
- **SC-008**: With the feed unavailable or nothing configured, the existing platform and Manager test suites pass
  unchanged.

## Assumptions

- **Dependencies**: Analyst Manager Registration, Analyst Manager Status Report and Fleet Read API, and Analyst Manager
  Minimum Version are implemented (archived under `specs/archive/`).
- **Versions**: The Manager reports a `MAJOR.MINOR.PATCH` build version and the minimum uses the same form; the pin and
  offered versions use it as well, compared numerically per part. A release whose version is not of that form is refused
  at verification with reason `version-malformed` and never offered. Prerelease labels are expressed by the channel, not by version suffixes, in this feature.
- **Channel label**: A release states its channel through the release source's own label (for GitHub, the prerelease
  flag of the release); the release workflow that sets it is out of scope.
- **Check interval**: The Manager asks for the offer at its existing periodic check interval; no separate schedule is
  introduced.
- **Offer scope**: The offer is the same for the whole stamp; per-registration rollout, rings, and staged rollout are
  out of scope.
- **Verification material**: A release publishes an SBOM and a GitHub artifact attestation (build provenance); the
  identities and trust roots against which the attestation is verified in production are deployment decisions and
  remain open. Development and Test use only explicit non-production values and the fake source.
- **Out of scope**: package download, background download, draining and applying an update (self-update), installers,
  Velopack and signing spikes, the publisher identity, the release workflow that publishes SBOMs and attestations,
  Manager-side signature verification, the production outbound GitHub access and production channel values, a Web UI
  or fleet-read display of offers, and anything the architecture marks as an open promotion item.
- **Architecture**: Implements [Updates](../../docs/architecture/analyst-manager.md#updates) (the stamp-served feed,
  channels, pinning, and attestation verification, without the Manager's download and self-update) and relies on
  [Machine Authentication](../../docs/architecture/analyst-manager.md#machine-authentication); it updates the statement
  that the feed does not exist.
