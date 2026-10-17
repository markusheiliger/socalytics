# Feature Specification: Web UI Analyst Registry Administration

**Feature Branch**: `20261017-185438-web-ui-registry-admin`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Web UI Analyst Registry Administration: the Analyst Registry is implemented as an API only (`src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryEndpoints.cs`: register and list Analyst images by digest with their cached `analyst-manifest.json` declarations, create, list, read, approve, and retire Analyst profile versions, define, list, and read workflow versions, and read and set the stamp's default workflow), restricted to Club Admins; Club Admins have no screen to operate it, so the stamp's analysis setup is only reachable with raw API calls. This feature adds a connected Registry administration area to the Web UI for Club Admins: browse registered Analyst images (digest, capability id and version, tier, scope, runtime and resource requirements, output schema, model and policy digests), register an image by its immutable digest and see validation problems from the platform explained, browse Analyst profiles and their versions with state (for example draft, approved, retired), create a profile version for a registered image, approve or retire a version with confirmation, browse workflow definitions and their versions with the dependency graph shown as a readable list of nodes and edges (required, optional, conditional), define a new workflow version, and view and change the default workflow with confirmation and optimistic concurrency (If-Match), with the effect stated plainly (only runs created afterwards use it; existing runs keep their snapshot). Non-admin members never see the area or its navigation entry, and the API stays the authority for every check. Everything goes through application-owned adapters over the generated Kiota client, with the existing navigation, i18n, accessibility, problem-handling, and online-only PWA rules. Out of scope: building, signing, or publishing Analyst images, image signature and provenance policy, graphical workflow editors, deleting registry records, registry changes for non-admin roles, and new or changed API operations unless the plan proves a read the UI needs is missing (then it belongs here and the plan refreshes the OpenAPI snapshot). Depends on Analyst Registry and Web UI Foundation (merged; archived under specs/archive/), Installable Web UI with Service Worker Updates (merged). Implements docs/architecture/client-applications.md#web-ui ("platform configuration and administration") and the operator-facing side of docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging and the registry/profile/workflow concepts it references."

## Clarifications

### Session 2026-10-17

- Q: When creating a profile version, may the Club Admin start a new profile id or only add versions to existing profiles? → A: Both: pick an existing profile id of the same capability (the platform assigns the next version) or enter a new profile id.
- Q: Should the approval confirmation point out when the Club Admin approves a version they created themselves? → A: Yes: the confirmation states that the Club Admin created this version and that the approval is recorded as a self-approval; it does not block the approval.
- Q: How should the workflow form set the requested capabilities? → A: They default to all capabilities of the definition's nodes and can be reduced to a subset of them.
- Q: How should the workflow form offer the completion policy? → A: As a choice of the completion policies the platform knows, today only "all requested capabilities", preselected; no free text.
- Q: When a new workflow version starts from an existing one whose nodes use profile versions that are no longer approved, what happens to those nodes? → A: They are kept and flagged, and the form cannot be submitted until each flagged node uses an approved profile version or is removed.
- Q: Where do the segmentation policy values of a new workflow version come from when it does not start from an existing version? → A: They are prefilled from the current default workflow's segmentation policy when a default exists; otherwise the Club Admin enters the version, segment duration in seconds, and SHA-256 digest.

## User Scenarios & Testing *(mandatory)*

The actor is the Club Admin, who today is the only role allowed to operate the stamp's Analyst Registry ([Analyst Registry](../archive/20261012-152148-analyst-registry/spec.md), FR-015 and FR-016). The area is a connected Web UI over the Registry operations that already exist. It implements "platform configuration and administration" of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) for the Registry, and the operator-facing side of [Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging) and [Analyst Profiles And Container-Owned Preprocessing](../../docs/architecture/analysts-models-and-hardware.md#analyst-profiles-and-container-owned-preprocessing). Everything the area changes is decided and checked by the platform; the Web UI explains the platform's decisions.

### User Story 1 - See the stamp's analysis setup (Priority: P1)

A Club Admin opens the Registry area from the navigation and sees which workflow is the stamp's default and since when, which Analyst images are registered, which Analyst profile versions exist in which state, and which workflow versions are defined. From each list the Club Admin opens one record and reads its details: for an image, its digest, repository, manifest digest, capability id and version, tier, execution scope, upstream capabilities, video access, output schema version, model digest, runtime constraints and accelerators, resource requirements, retry and timeout minimums, and when and by whom it was registered; for a profile version, its state, profile digest, image and model digests, every bound value, and who created, approved, or retired it and when; for a workflow version, its requested capabilities, completion policy, segmentation policy, definition digest, and its nodes and dependency edges as a readable list.

**Why this priority**: It replaces raw API calls for the most frequent need, finding out what the stamp will run, and every other story builds on these views. It changes nothing and is valuable on its own.

**Independent Test**: With the development Registry seed and one additional registered image, a Club Admin opens the area, sees the default workflow and the three lists with their records, opens one image, one profile version, and one workflow version, and sees every listed field; a Coach sees neither the navigation entry nor the area, and opening its address shows the not-permitted view.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin, **When** the navigation is shown, **Then** it contains an entry for the Registry area; **Given** any member without the Club Admin role, **Then** it does not, and opening any Registry address shows the not-permitted view.
2. **Given** the Registry area, **When** it loads, **Then** it shows the current default workflow (id, version, when and by whom it was set) or states plainly that no default is set and that new analysis runs cannot be created until one is, and links to the images, profiles, and workflows lists.
3. **Given** registered images, **When** the Club Admin opens the images list, **Then** each image is listed with its capability id and version, a shortened digest, and its registration time, and can be filtered by capability id.
4. **Given** a registered image, **When** the Club Admin opens it, **Then** the view shows its full declaration as listed in this story, with digests shortened and the full values available to read and copy, and the full manifest available on request as plain text exactly as stored.
5. **Given** Analyst profile versions, **When** the Club Admin opens the profiles list, **Then** the versions are grouped by profile id, each with its version number, state (draft, approved, retired), capability id and version, and shortened image digest, and the list can be filtered by state and by capability id.
6. **Given** a workflow version, **When** the Club Admin opens it, **Then** the view lists every node with its node key and the profile id and version it uses, and every edge as "upstream node → dependent node" with its kind (required, optional, conditional) and, for a conditional edge, its condition in words, and marks whether this version is the current default.
7. **Given** a record that does not exist, **When** the Club Admin opens its address, **Then** the not-found view is shown.

---

### User Story 2 - Register an Analyst image by digest (Priority: P1)

A Club Admin receives a digest-pinned image reference from an Analyst author and registers it in the Registry area. The platform imports the image's manifest; the Web UI shows the resulting declaration, or, when the platform refuses, explains in plain words why (for example: the reference uses a tag instead of a digest, lies outside the allowed registry, the image was not found, the registry is unavailable and the attempt can be repeated, the manifest is missing or invalid with its listed problems, the labels contradict the manifest, or the layer format is not supported). Registering an image that is already registered shows the stored declaration and says that nothing changed.

**Why this priority**: Registration is the entry point of every new capability version; without it, profiles and workflows cannot change through the Web UI.

**Independent Test**: Against the platform's image registration, a Club Admin submits a tag reference, a reference outside the allowed registry, and a valid digest reference twice, and sees the tag and registry explanations before or after submission, the new declaration after the first valid submission, and the "already registered" message after the second.

**Acceptance Scenarios**:

1. **Given** the registration form, **When** the Club Admin enters a reference without a digest, **Then** the Web UI explains that only immutable digest references are accepted before sending anything.
2. **Given** a digest reference, **When** the Club Admin submits it, **Then** the form shows that registration is in progress, prevents a second submission, and on success opens the registered image's details.
3. **Given** the platform refuses the registration, **When** the refusal arrives, **Then** the form keeps the entered reference and shows a worded explanation for the refusal reason, including every validation problem the platform lists, and states whether trying again later can help.
4. **Given** an already registered digest, **When** the Club Admin submits it again, **Then** the Web UI shows the stored declaration and states that the image was already registered and nothing changed.

---

### User Story 3 - Create, approve, and retire Analyst profile versions (Priority: P2)

A Club Admin creates a new version of an Analyst profile for a registered image: the form starts from the image's declaration (capability, model digest, runtime constraints, accelerators, resources, retry and timeout minimums, output schema version, and the schema versions of its upstream capabilities) so that only the execution choices need adjusting. The platform creates a draft version or explains which values contradict the declaration. The Club Admin then approves the draft, after a confirmation that names the profile version, its image and model digests, and that approved versions never change. Later the Club Admin retires a version that should no longer be used in new workflows, after a confirmation that explains the consequence; the platform refuses to retire a version the current default workflow uses, and the Web UI explains that.

**Why this priority**: Profiles are the reviewed execution choices that workflows reference; approving and retiring them completes the Registry's governance loop. It depends on registered images (Story 2) for creation, while approving and retiring work on seeded profiles alone.

**Independent Test**: A Club Admin creates a profile version from a registered image, sees it as a draft with its profile digest, submits a contradicting value and sees the named contradictions, approves the draft after confirmation, tries to retire a profile version used by the default workflow and sees the refusal explained, and retires an unused version after confirmation.

**Acceptance Scenarios**:

1. **Given** a registered image, **When** the Club Admin starts a new profile version from it (from the image's details or the profiles list), **Then** the form is prefilled from the declaration, the image and capability cannot be changed in the form, the Club Admin either picks an existing profile id of the same capability or enters a new profile id, and every field shows the declaration's minimum or allowed values where the declaration states them.
2. **Given** a completed form, **When** the Club Admin submits it, **Then** a new draft version is created and shown with its version number and profile digest; when the same content already exists as a draft or approved version, the Web UI shows that version and says that no new version was created.
3. **Given** values that contradict the declaration, **When** the platform refuses them, **Then** the form keeps the entered values and lists each contradiction the platform names next to the field it concerns where the field is known.
4. **Given** a draft version, **When** the Club Admin approves it and confirms, **Then** the version becomes approved and the view shows who approved it and when; cancelling the confirmation changes nothing; when the Club Admin created the version, the confirmation says so and that the approval is recorded as a self-approval, without blocking it.
5. **Given** an approved or draft version that the current default workflow does not use, **When** the Club Admin retires it and confirms, **Then** the version becomes retired and the confirmation has stated that retired versions cannot be used in new workflow versions while existing workflow versions and runs keep them.
6. **Given** a version used by the current default workflow, a version that is not a draft (for approval), or an already retired version, **When** the platform refuses the change, **Then** the Web UI explains the reason and shows the version's current state.
7. **Given** someone else changed the version since it was loaded, **When** the Club Admin confirms an approval or retirement, **Then** the Web UI says that the version has changed, shows its current state, and lets the Club Admin decide again; it never repeats the change automatically.

---

### User Story 4 - Define a workflow version and change the default workflow (Priority: P2)

A Club Admin defines a new workflow version from approved profile versions: requested capabilities, completion policy, segmentation policy, nodes (each a node key and an approved profile version), and dependency edges (upstream node, dependent node, kind, and for conditional edges a condition), entered as structured lists, optionally starting from an existing workflow version. The platform validates the definition and either stores it or lists its violations (for example a non-approved profile, a missing required upstream capability, incompatible schema versions, a cycle, an unknown completion policy, or an invalid segmentation policy), which the Web UI explains. The Club Admin then makes the new version the stamp's default after a confirmation that states plainly that only analysis runs created afterwards use it and existing runs keep their snapshot; when someone else changed the default in the meantime, the Web UI says so and shows the current default instead of overwriting it.

**Why this priority**: Changing what new analysis runs execute is the outcome a Club Admin operates the Registry for; it builds on approved profiles (Story 3).

**Independent Test**: A Club Admin defines a new version from the seeded approved profiles, submits a definition with a cycle and sees the violation explained, stores a valid version, makes it the default after confirmation, sees the default updated, and in a second session with the old default loaded receives the "default changed" explanation instead of an overwrite.

**Acceptance Scenarios**:

1. **Given** approved profile versions, **When** the Club Admin defines a workflow version, **Then** nodes can only be chosen from approved profile versions, edges and condition targets can only refer to nodes of the same definition, the requested capabilities default to all capabilities of the nodes and can be reduced to a subset of them, and the completion policy is chosen from the policies the platform knows (today only "all requested capabilities", preselected).
2. **Given** an existing workflow version, **When** the Club Admin starts a new version from it, **Then** the form is prefilled with its content, the workflow id is kept, a new version identifier must be entered, and nodes whose profile versions are no longer approved are flagged and block submission until each uses an approved profile version or is removed.
3. **Given** an invalid definition, **When** the platform refuses it, **Then** the form keeps the entered content and explains each violation in words, pointing to the node it concerns where the platform names one; a version identifier that already exists is explained as such.
4. **Given** a stored workflow version, **When** the Club Admin makes it the default and confirms, **Then** the default changes, the confirmation has stated that only runs created afterwards use it and existing runs keep their snapshot, and the area shows the new default with who set it and when.
5. **Given** a workflow version that cannot be resolved (for example because a profile it uses was retired), **When** the Club Admin tries to make it the default, **Then** the Web UI explains that the platform cannot resolve it and the default stays unchanged.
6. **Given** the default changed since the Club Admin loaded it, **When** the Club Admin confirms a change, **Then** the Web UI says that the default has changed, shows the current default, and lets the Club Admin decide again.
7. **Given** no default is set yet, **When** the Club Admin sets one, **Then** the confirmation states that new analysis runs become possible with it, and the change succeeds without a previously loaded default.

---

### User Story 5 - Use the area safely in every supported setting (Priority: P3)

The Registry area follows the Web UI's existing rules: it uses the same navigation, language keys, problem handling, and accessibility as the rest of the Web UI, works with keyboard and screen readers, and is online only, so an offline browser or an installed app without network shows the connection-required state and no Registry data is cached or stored in the browser.

**Why this priority**: These are the Web UI's existing quality rules applied to the new views; they protect access control, accessibility, and the PWA's no-local-data rule.

**Independent Test**: Automated browser tests reach every Registry view and action by keyboard, run accessibility rule checks on each view, dialog, and error state, check that no Registry response is served from service-worker caches or found in browser storage, and check that an offline browser shows the connection-required state.

**Acceptance Scenarios**:

1. **Given** any Registry view, form, dialog, or state (loading, data, empty, refused, error), **When** automated accessibility rule checks run, **Then** they report no violations, and every control is operable by keyboard with visible focus and announced results.
2. **Given** the browser is offline, **When** the Club Admin opens or submits anything in the area, **Then** the connection-required state is shown and nothing is queued for later.
3. **Given** the Club Admin used the area, **When** browser storage and the service-worker caches are inspected, **Then** they contain no Registry responses.
4. **Given** the Club Admin role is removed while the area is open, **When** the next read or change is made, **Then** the platform's refusal leads to the not-permitted view.

### Edge Cases

- Empty Registry (no images, profiles, or workflows, and no default): each list states that it is empty and how it is filled, and the area states that no default workflow is set.
- A capability declaration, profile, or workflow value the Web UI does not know (for example a new tier, scope, state, edge kind, condition kind, or completion policy) is shown with its raw value instead of being hidden or failing.
- Very long lists (many images or profile versions) remain usable; filtering by capability id and state narrows them.
- Manifests or validation details that contain markup, very long text, or unexpected structure are shown as plain text within bounded space and never interpreted.
- A profile version created from an image whose declaration has no upstream capabilities has no input schema versions to set.
- A conditional edge whose condition refers to another node or capability is described with that node or capability by name.
- The registration of a large image takes long: the form keeps showing progress and remains cancellable from the Club Admin's view by leaving the page, without a duplicate submission.
- The platform answers a change with "too many requests", "unavailable", or a server error: the Web UI shows a recoverable error and changes nothing locally.
- A session that ends while a form is open follows the Web UI's existing signed-out handling; unsaved form content is not kept.
- Two Club Admins change the same profile version or the default at the same time: exactly one change succeeds, and the other sees the "has changed" explanation.
- A record address that is malformed (for example an invalid digest or version) shows the not-found view.

## Requirements *(mandatory)*

### Functional Requirements

#### Navigation and access

- **FR-001**: The Web UI MUST offer a Registry area with its own navigation entry, shown only to members with the Club Admin role; every Registry view MUST be addressable, and opening any Registry address without the Club Admin role MUST show the not-permitted view without loading Registry data.
- **FR-002**: The platform's authorization MUST stay authoritative: a refusal for lack of permission MUST show the not-permitted view, and a missing record MUST show the not-found view.

#### Overview and reads

- **FR-003**: The area's entry view MUST show the current default workflow (id, version, set at, set by) with a link to its definition, or state that no default is set and that analysis runs cannot be created until one is; it MUST link to the images, profiles, and workflows lists.
- **FR-004**: The images list MUST show every registered image with capability id and version, shortened image digest, and registration time, and MUST support filtering by capability id.
- **FR-005**: An image's view MUST show its image digest, repository, manifest digest, capability id and version, tier, execution scope, upstream capabilities with their versions, schema versions, and kinds, video access, output schema version, compatible profiles, model digest, runtime constraints, accelerators, resources, retry and timeout minimums, determinism, registration time, and registering member as the platform stores them, and MUST offer the full stored manifest as plain text on request.
- **FR-006**: The profiles list MUST show every profile version grouped by profile id with version, state, capability id and version, and shortened image digest, and MUST support filtering by state and by capability id.
- **FR-007**: A profile version's view MUST show its state, profile digest, image and model digests, capability id and version, runtime constraints, accelerators, resources, maximum attempts, lease, stale-timeout, and execution-timeout values, video policy, input and output schema versions, and the creation, approval, and retirement times and members where present.
- **FR-008**: The workflows list MUST show every workflow version with workflow id, version, shortened definition digest, creation time, and whether it is the current default.
- **FR-009**: A workflow version's view MUST show its requested capabilities, completion policy, segmentation policy (version, duration, digest), definition digest, creation time and member, a list of its nodes (node key, profile id and version, linked to the profile version), and a list of its edges, each as upstream node → dependent node with its kind (required, optional, conditional) and, for conditional edges, the condition described in words.
- **FR-010**: Digests MUST be displayed in a shortened, recognizable form with the full value available to read and copy.

#### Changes

- **FR-011**: The Club Admin MUST be able to register an image by reference; the Web UI MUST explain before submission that only digest references are accepted when the entered reference has no digest, MUST prevent duplicate submissions while a registration is in progress, MUST show the declaration on success, and MUST state when the image was already registered and nothing changed.
- **FR-012**: The Web UI MUST explain every image registration refusal reason the platform defines (`reference-not-digest`, `reference-not-allowed`, `image-not-found`, `registry-unavailable`, `digest-mismatch`, `manifest-missing`, `manifest-invalid`, `labels-mismatch`, `unsupported-media-type`) in words, list the platform's validation details, and say whether retrying later can help.
- **FR-013**: The Club Admin MUST be able to create a profile version for a registered image with a form prefilled from the image's declaration; the image and capability MUST be fixed by the chosen image; the profile id MUST be either an existing profile id of the same capability (the platform assigns the next version) or a new profile id; the Web UI MUST show the created draft with its version and profile digest, MUST state when an identical draft or approved version already existed and no new version was created, and MUST explain the platform's refusals: each contradiction the platform lists with the field it concerns (`profile-contradicts-declaration`), an image that is not registered (`image-not-registered`), and identical content that already exists under another profile id or as a retired version (`profile-content-exists`).
- **FR-014**: The Club Admin MUST be able to approve a draft profile version and to retire a draft or approved profile version, each only after a confirmation that names the version and its image and model digests and states the consequence (approved versions never change; retired versions cannot be used in new workflow versions, while existing workflow versions and analysis runs keep them); the approval confirmation MUST state when the approving Club Admin created the version and that the approval is recorded as a self-approval, without blocking it.
- **FR-015**: The Club Admin MUST be able to define a workflow version with requested capabilities (defaulting to all capabilities of the nodes and reducible to a subset of them), a completion policy chosen from the policies the platform knows (today only "all requested capabilities", preselected), a segmentation policy (version, segment duration in seconds, and SHA-256 digest, prefilled from the current default workflow when a default exists and the form does not start from an existing version), nodes chosen from approved profile versions, and edges between nodes of the same definition with kind and, for conditional edges, a condition; the Club Admin MUST be able to start from an existing workflow version, in which case nodes whose profile versions are no longer approved MUST be flagged and MUST block submission until each uses an approved profile version or is removed; on refusal the Web UI MUST keep the entered content and explain in words each violation code the platform names (`profile-not-found` for a named node, `profile-not-approved`, `missing-upstream`, `schema-incompatible`, `cycle`, `invalid-graph`, `requested-capability-missing`, `unknown-completion-policy`, `invalid-segmentation-policy`) and an already existing version identifier (`workflow-version-exists`).
- **FR-016**: The Club Admin MUST be able to make a workflow version the default only after a confirmation that states that only analysis runs created afterwards use it and existing runs keep their snapshot; when no default exists the change MUST succeed without a previously loaded default, and a workflow version the platform cannot resolve MUST be explained without changing the default.
- **FR-017**: Approvals, retirements, and default changes MUST be conditional on the version the Club Admin last saw; when the record changed in the meantime, the Web UI MUST say so, show the current state, and require a new decision, and MUST never repeat a change automatically.
- **FR-018**: The Web UI MUST offer no deletion of Registry records, no editing of approved or retired profile versions or stored workflow versions, and no building, signing, or publishing of images.

#### Platform access, client rules, and quality

- **FR-019**: The Web UI MUST reach the platform only through application-owned adapters over the generated client and only through the existing Registry operations (`registerAnalystImage`, `listAnalystImages`, `getAnalystImage`, `createAnalystProfile`, `listAnalystProfiles`, `getAnalystProfile`, `approveAnalystProfile`, `retireAnalystProfile`, `defineWorkflow`, `listWorkflows`, `getWorkflow`, `getDefaultWorkflow`, `setDefaultWorkflow`); it MUST NOT add other platform operations unless the plan shows a read is missing. The platform's OpenAPI document MUST describe the request and response bodies of these operations so the generated client has models for them, without changing their behavior.
- **FR-020**: The Registry area MUST be online only: Registry responses MUST NOT be cached by the service worker or stored in browser storage, changes MUST NOT be queued, and an offline browser or an installed app without network MUST show the connection-required state.
- **FR-021**: All new user-facing text MUST come from translation keys with English as the only shipped language, all new views and dialogs MUST be operable by keyboard with visible focus, announce results to assistive technology, and pass automated WCAG 2.2 AA rule checks, and all platform-provided text (manifests, details, identifiers) MUST be rendered as text, never as markup.
- **FR-022**: Values the Web UI does not know (tiers, scopes, states, edge kinds, condition kinds, completion policies, refusal reasons) MUST be shown with their raw value rather than hidden or failing.
- **FR-023**: Automated tests MUST cover the adapters' mapping of declarations, profiles, workflows, and the default, every refusal explanation, the confirmations and their cancellation, the "has changed" handling, role-based access, accessibility rule checks, the online-only rule, and a browser test against the running platform that browses the seeded Registry, creates and approves a profile version, defines a workflow version, and makes it the default.

### Key Entities *(include if feature involves data)*

- **Capability Declaration**: A registered Analyst image as the platform stores it: image digest, repository, manifest digest, capability, upstream capabilities, requirements, model digest, the stored manifest, and when and by whom it was registered.
- **Analyst Profile Version**: A versioned execution choice for one capability bound to one registered image and its model: state (draft, approved, retired), profile digest, bound values, and lifecycle times and members.
- **Workflow Version**: An immutable workflow definition: requested capabilities, completion and segmentation policies, nodes referencing profile versions, dependency edges, definition digest, and creation time and member.
- **Default Workflow**: The stamp's single current default workflow version with when and by whom it was set; new analysis runs resolve it at creation.
- **Registry Refusal**: The platform's named reason for refusing a change, with its listed details (validation problems, contradictions, or violations).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Club Admin finds the current default workflow and its nodes in at most 2 navigation steps from the Web UI's home view.
- **SC-002**: A Club Admin goes from a digest reference to an approved profile version in a new workflow version that is the stamp's default without any raw API call, in at most 15 minutes in a usability walkthrough.
- **SC-003**: In automated tests, every refusal reason of FR-012 and FR-015 and every profile and default refusal is shown with its own worded explanation in 100% of its scenarios.
- **SC-004**: In automated tests, members without the Club Admin role never see the navigation entry and are refused every Registry view in 100% of cases.
- **SC-005**: Automated accessibility rule checks report zero violations for every Registry view, form, dialog, and state, and every Registry action is reachable by keyboard.
- **SC-006**: In automated tests, a concurrent change by another Club Admin is never overwritten: 100% of stale approvals, retirements, and default changes end with the "has changed" explanation and no change.
- **SC-007**: In automated tests, no Registry response is found in service-worker caches or browser storage after a session that used the area.

## Assumptions

- The Club Admin role stays the only role allowed to operate the Registry until a stamp-operator role exists ([Analyst Registry](../archive/20261012-152148-analyst-registry/spec.md), FR-015); read-only Registry views for other roles are out of scope.
- The Web UI uses the Registry operations as they exist, with their refusal codes and concurrency rules as defined by [Analyst Registry](../archive/20261012-152148-analyst-registry/spec.md) and its [Registry API contract](../archive/20261012-152148-analyst-registry/contracts/registry-api.md); no new or changed platform behavior is expected. The platform's OpenAPI document gains request and response schemas for these existing operations, which today it lists without bodies, so the generated client has models for them; their paths, statuses, bodies, and rules stay unchanged.
- "Model and policy digests" are shown as far as the platform reports them: the model, manifest, image, profile, definition, and segmentation-policy digests. Further policy digests that the manifest does not carry today are out of scope.
- Lists are shown in full as the platform returns them (the Registry operations do not page); filtering happens through the operations' existing query parameters and in the browser for presentation only.
- The form for a profile version offers the declaration's values as defaults; whether a value is allowed is decided by the platform.
- Workflow definition is a structured form with lists of nodes and edges; graphical editing, drag and drop, and visual graph rendering are out of scope.
- The development and test Registry seed (one default workflow with approved profiles and their declarations) is the starting state of browser tests against the running platform; because the local platform has no image registry, the platform browser test registers the digest of a seeded declaration (answered as already registered), and successful new registrations and registry refusals are covered against the simulated platform and by the platform's existing image import tests.
- Out of scope: building, signing, or publishing Analyst images; image signature and provenance policy; deleting Registry records; Registry changes or reads for non-admin roles; offline mode; and new or changed platform operations unless the plan shows a read is missing.
- Dependencies: the merged [Analyst Registry](../archive/20261012-152148-analyst-registry/spec.md), [Web UI Foundation](../archive/20261012-150657-web-ui-foundation/spec.md), and [Installable Web UI with Service Worker Updates](../archive/20261015-194113-web-ui-pwa-updates/spec.md). The Web UI environment ([Web UI Verification Environment](../archive/20261012-145505-web-ui-environment/spec.md)) already provides Node.js, Kiota, and Playwright; no new framework, SDK, or tool is expected. The batch siblings [Web UI Connected Analysis Review](../20261017-181118-web-ui-analysis-review/spec.md) and [Analysis Run Cancellation](../20261017-182600-analysis-run-cancellation/spec.md) change other Web UI screens and are not dependencies.
