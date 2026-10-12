# Feature Specification: Analyst Registry: Manifests, Profiles, and Workflow Resolution

**Feature Branch**: `20261012-152148-analyst-registry`

**Created**: 2026-10-12

**Status**: Draft

**Input**: User description: "Analyst Registry: Manifests, Profiles, and Workflow Resolution. Replace today's configuration-only workflow definitions with a durable Registry in the platform, as the architecture's queryable Analyst manifest cache keyed by immutable image digest describes. (1) Manifest import: an authorized operator registers an Analyst image by immutable digest from the stamp's configured OCI registry (GHCR); the platform reads the versioned analyst-manifest.json and the io.socalytics.* labels from the image, validates them against a new versioned JSON Schema contract, and caches the capability declaration immutably by image digest; changed metadata always means a new image digest. (2) Analyst profiles: versioned, digest-addressed bindings of one capability version to one image digest and one model digest with runtime, accelerator, and resource requirements, retry, lease, stale-timeout, and execution-timeout values, video policy, and schema versions; draft → approved → retired; approval is a separately authorized, audited operation; approved profiles are never edited, only superseded. (3) Workflow definitions: versioned workflow definitions whose nodes reference approved profiles, with edges and dependency checks derived from the declared upstream capabilities, and one default workflow per stamp that analysis-run creation resolves from the Registry instead of configuration; the configured definition remains only as a development and test seed. Read APIs for manifests, profiles, and workflows. Until a stamp-operator role exists, registration and approval are restricted to Club Admins, as for other operational APIs. Out of scope: real Analyst images and capabilities, license, quality, and runtime evidence gates for production promotion, mutable profile aliases, the UI, signature and provenance verification of images, and deriving the Manager's per-capability consumers from the Registry."

## Clarifications

### Session 2026-10-12

- Q: Should the Registry be one spec, or should workflow resolution become its own follow-up spec? → A: One spec with all three stories; they remain separately testable increments within it.
- Q: May the same Club Admin who created a profile also approve it? → A: Yes; creation and approval remain separate audited operations, and the approval's audit event marks self-approval.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Register an Analyst image and cache its declaration (Priority: P1)

An operator (today a Club Admin) registers a published Analyst image by its immutable digest. The platform fetches the image's metadata from the stamp's configured image registry, reads the embedded Analyst manifest and the SocAlytics labels, validates them, and stores the capability declaration under the image digest. From then on, the declaration can be queried without contacting the image registry again, and it never changes.

**Why this priority**: Every profile and workflow in the Registry rests on validated, immutable capability declarations. Without them, run snapshots keep depending on hand-written configuration.

**Independent Test**: Publish a test image with a valid manifest to a disposable image registry, register it by digest, and query the stored declaration; then try a tag instead of a digest, an image without a manifest, an invalid manifest, and labels that contradict the manifest, and observe that each is refused with a clear reason and nothing is stored.

**Acceptance Scenarios**:

1. **Given** a published image with a valid Analyst manifest, **When** an operator registers it by digest, **Then** the declaration (capability ID and version, tier, execution scope, upstream capabilities and schema versions, video policy, output schema version, compatible profiles, image and model digests, runtime and hardware constraints, resource, retry, lease, stale-timeout, and execution-timeout requirements, determinism expectations) is stored under the image digest and returned.
2. **Given** an image that is already registered, **When** it is registered again, **Then** the existing declaration is returned unchanged and no duplicate is created.
3. **Given** a reference by tag instead of digest, **When** it is registered, **Then** the registration is refused because only immutable digests are accepted.
4. **Given** an image without an Analyst manifest, with a manifest that fails schema validation, or whose SocAlytics labels contradict the manifest, **When** it is registered, **Then** the registration is refused with the specific reason and nothing is stored.
5. **Given** the image registry is unreachable or refuses access, **When** an image is registered, **Then** the registration fails with a retryable reason and nothing is stored.
6. **Given** a member without the operator authority, **When** they try to register an image, **Then** the request is refused and the attempt is audited.
7. **Given** registered declarations, **When** an authorized member lists or reads them, **Then** they see each declaration with its image digest and registration time, without contacting the image registry.

---

### User Story 2 - Create, approve, and retire Analyst profiles (Priority: P2)

An operator creates an Analyst profile that binds one capability version to one registered image digest and its model digest, together with the runtime, accelerator, resource, retry, lease, stale-timeout, and execution-timeout values. The profile starts as a draft. A separate, audited approval makes it usable by workflows. Approved profiles never change; a different binding needs a new profile version. Retiring a profile keeps it in existing run snapshots but stops new workflows from using it.

**Why this priority**: The architecture resolves exactly one approved profile per capability for each run. Profiles are the unit of approval between a declaration and executable work. They need registered declarations (Story 1).

**Independent Test**: Create a profile against a registered declaration, approve it, try to change it, create a second version, retire the first, and confirm the profile digests, states, and audit events.

**Acceptance Scenarios**:

1. **Given** a registered declaration, **When** an operator creates a profile for its capability version with values within the declaration's requirements, **Then** a draft profile with a profile ID, version, and digest computed from its content is stored.
2. **Given** a profile whose capability, image, model, schema versions, or requirements contradict the referenced declaration (for example a timeout below the declared minimum or a model digest that is not the image's), **When** it is created, **Then** it is refused with the specific reason.
3. **Given** a draft profile, **When** an operator (including its creator) approves it, **Then** it becomes approved in a separately audited operation that marks self-approval when the approver created it; **and When** anyone tries to change an approved profile, **Then** the change is refused and a new profile version is required.
4. **Given** an approved profile, **When** an operator retires it, **Then** it can no longer be used in new or changed workflow definitions, while run snapshots that already pinned it are unaffected.
5. **Given** a member without the operator authority, **When** they try to create, approve, or retire a profile, **Then** the request is refused and audited.

---

### User Story 3 - Define workflows and resolve runs from the Registry (Priority: P3)

An operator defines a versioned workflow from approved profiles: which capabilities are requested, the segmentation policy, the completion policy, and the dependencies between nodes, which must match the upstream capabilities the declarations require. One workflow version is the stamp's default. When a Coach finalizes a recording set, analysis-run creation resolves the default workflow from the Registry and pins every node's approved profile in the immutable run snapshot. Changing the default never changes runs that already exist.

**Why this priority**: This is where the Registry replaces the configured definition. It needs approved profiles (Story 2).

**Independent Test**: Define a workflow from two approved profiles where one depends on the other, make it the default, finalize a recording set, and confirm that the new run's snapshot pins the profiles, image, and model digests from the Registry; then make another version the default and confirm the earlier run is unchanged.

**Acceptance Scenarios**:

1. **Given** approved profiles, **When** an operator defines a workflow whose dependencies satisfy every declared required upstream capability and schema version, **Then** a versioned workflow definition is stored.
2. **Given** a workflow whose nodes reference draft or retired profiles, miss a required upstream capability, use incompatible schema versions, or form a cycle, **When** it is defined, **Then** it is refused with the specific reason.
3. **Given** a stored workflow definition, **When** an operator makes it the stamp's default, **Then** new analysis runs resolve it, and the change is audited.
4. **Given** a default workflow, **When** analysis-run creation resolves it, **Then** the run and node snapshots contain exactly the approved profiles' identities, digests, and requirement values, as they do today with the configured definition.
5. **Given** existing runs, **When** the default workflow or a profile changes or is retired, **Then** those runs' snapshots stay unchanged.
6. **Given** no default workflow is set, or it can no longer be resolved, **When** a recording set is finalized, **Then** run creation behaves as it does today for a missing or invalid configured definition: no run is created, and the condition is visible to operators.
7. **Given** a development or test environment with a configured workflow definition and an empty Registry, **When** the platform starts, **Then** the configured definition seeds the Registry once as approved profiles and a default workflow; in any other environment, the configured definition is not used.

### Edge Cases

- The same capability version is declared by two different images: both declarations are stored under their own image digests; profiles choose explicitly.
- A manifest declares a model digest that differs from the label: refused (Story 1, scenario 4).
- An image registry returns a different digest than requested: refused; only content that matches the requested digest is stored.
- A very large image: only its metadata (manifest, configuration, and the layer that contains the Analyst manifest) is read; image layers with model weights are not downloaded.
- Two operators approve the same draft at once: one approval wins; the other is told the profile is already approved.
- A workflow version is set as default while a run is being created: the run uses either the old or the new default completely, never a mix.
- A retired profile is still referenced by the current default workflow: retiring is refused until the default no longer uses it.
- The configured seed differs from what the Registry already holds: the Registry wins; the seed is applied only to an empty Registry.

## Requirements *(mandatory)*

### Functional Requirements

#### Manifest import

- **FR-001**: The system MUST let an authorized operator register an Analyst image only by an immutable digest reference in the stamp's configured image registry, and MUST refuse tag references.
- **FR-002**: The system MUST read the image's Analyst manifest and its SocAlytics labels without downloading layers that the manifest does not need, and MUST verify that every piece of content it reads matches its referenced digest.
- **FR-003**: The system MUST validate the manifest against a versioned, machine-readable Analyst manifest contract in the repository's canonical contract location, and MUST refuse manifests that fail validation or whose labels contradict them, storing nothing.
- **FR-004**: The system MUST store each accepted capability declaration immutably under its image digest with its registration time and registering operator; registering the same digest again MUST return the stored declaration without change.
- **FR-005**: Registration failures caused by the image registry (unreachable, refused, not found) MUST be reported as such and MUST store nothing.

#### Analyst profiles

- **FR-006**: The system MUST let an authorized operator create a versioned Analyst profile that binds one capability version to one registered image digest and its declared model digest, with runtime, accelerator, resource, retry, lease, stale-timeout, and execution-timeout values, video policy, and input and output schema versions; the profile MUST receive a digest computed from its content.
- **FR-007**: The system MUST refuse profiles whose bindings or values contradict the referenced declaration.
- **FR-008**: Profiles MUST move only through the states draft, approved, and retired; approval and retirement MUST be separately authorized, audited operations; the creator of a profile MAY approve it, and the approval's audit event MUST mark self-approval; approved and retired profiles MUST NOT change.
- **FR-009**: A retired profile MUST NOT be used in new or changed workflow definitions; retiring a profile used by the current default workflow MUST be refused.

#### Workflow definitions and resolution

- **FR-010**: The system MUST let an authorized operator define a versioned workflow from approved profiles, with requested capabilities, a segmentation policy, a completion policy, and node dependencies, and MUST refuse definitions that reference non-approved profiles, miss a declared required upstream capability, use incompatible schema versions, or contain a cycle.
- **FR-011**: Exactly one workflow version MUST be the stamp's default at any time once one is set; changing it MUST be audited and atomic.
- **FR-012**: Analysis-run creation MUST resolve the default workflow from the Registry and pin the approved profiles' identities, digests, and requirement values in the run and node snapshots, with the same snapshot content and immutability as today.
- **FR-013**: When no default workflow is set or it cannot be resolved, run creation MUST behave as it does today for a missing or invalid configured definition.
- **FR-014**: In development and test environments only, a configured workflow definition MUST seed an empty Registry once as approved profiles and a default workflow; in any other environment the configured definition MUST NOT be used.

#### Access, audit, and reads

- **FR-015**: Registering images, creating, approving, and retiring profiles, defining workflows, and changing the default MUST be restricted to Club Admins until a stamp-operator role exists, and MUST each record an audit event with the operator, the affected identities and digests, and the outcome.
- **FR-016**: The system MUST provide read operations for declarations, profiles (with state), workflow definitions, and the current default, available to Club Admins, that never contact the image registry.

### Key Entities

- **Capability declaration**: The imported, immutable content of an Analyst manifest, keyed by image digest: capability ID and version, tier, execution scope, upstream capabilities and schema versions, video policy, output schema version, compatible profile identities, image and model digests, runtime and hardware constraints, resource and timing requirements, determinism expectations.
- **Analyst profile**: A versioned, digest-addressed binding of one capability version to one image and model digest pair with concrete requirement values; states draft, approved, retired.
- **Workflow definition**: A versioned set of nodes (each an approved profile) and edges with requested capabilities, segmentation policy, and completion policy.
- **Default workflow**: The stamp's single current workflow version used by run creation.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of runs created after this feature merges pin profiles, images, and models that exist as approved profiles in the Registry; 0 runs outside development and test resolve the configured definition.
- **SC-002**: 0 stored declarations, approved profiles, or workflow versions change after they are stored, across all tests.
- **SC-003**: 100% of invalid manifests, contradicting labels, tag references, digest mismatches, contradicting profiles, and invalid workflows in the tests are refused with a specific reason and store nothing.
- **SC-004**: Registering a typical Analyst image reads at most its manifest, configuration, and the one layer that holds the Analyst manifest; no model-weight layer is downloaded in the tests.
- **SC-005**: 100% of state-changing Registry operations produce an audit event with the operator and the affected digests.
- **SC-006**: Existing runs' snapshots are byte-identical before and after every change to the default workflow or to profiles in the tests.

## Assumptions

- **Actors**: Club Admins act as operators until a stamp-operator role is defined, as `platform-implementation.md` states for operational APIs; no new role is introduced.
- **Image registry**: Each stamp configures one image registry (GHCR in production; a disposable registry in tests) and, when needed, read credentials as protected configuration. Image signature and provenance verification is out of scope and follows with the supply-chain work.
- **Manifest location**: The Analyst manifest is copied into the image and supplies `io.socalytics.*` labels, as the architecture describes; the exact label names and the file path inside the image are defined by the plan and the Analyst manifest contract.
- **Approval meaning**: Approval in this slice records an accountable decision; the license, quality, runtime, and cost evidence gates that production promotion requires remain governed by the architecture's catalog rules and are out of scope.
- **Run snapshots**: The snapshot content and the job contract stay unchanged; only their source moves from configuration to the Registry.
- **Out of scope**: real Analyst images and capabilities; mutable profile aliases (a run always pins a concrete profile); the Web UI; image signature and provenance verification; deriving the Analyst Manager's per-capability queue consumers from the Registry (follows the Analyst Manager Work Execution feature); deleting declarations, profiles, or workflows.
- **Evidence**: Passing validation is development evidence only and does not establish deployment support or production readiness.
- **Dependencies**: `specs/archive/20261005-130703-durable-analysis-workflow` provides run creation, snapshots, the workflow resolver, and the job contracts. `specs/archive/20261005-130701-club-identity-foundation` provides Club Admin authorization and audit. `specs/archive/20261005-130700-platform-persistence-foundation` provides storage, migrations, and units of work.
- **Architecture References**: [docs/architecture/analysts-models-and-hardware.md](../../docs/architecture/analysts-models-and-hardware.md) (Image Metadata And Packaging; Analyst Profiles And Container-Owned Preprocessing; Capability Declaration), [docs/architecture/analyst-capability-catalog.md](../../docs/architecture/analyst-capability-catalog.md) (Catalog Rules), [docs/architecture/platform-implementation.md](../../docs/architecture/platform-implementation.md) (Planned Data Organization; API And Identity), [docs/architecture/job-processing.md](../../docs/architecture/job-processing.md) (Durable Workflow Model), [docs/architecture/contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md) (Semantic Ownership).
