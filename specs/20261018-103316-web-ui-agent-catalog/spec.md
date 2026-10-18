# Feature Specification: Web UI Agent Catalog Administration

**Feature Branch**: `20261018-103316-web-ui-agent-catalog`

**Created**: 2026-10-18

**Status**: Draft

**Input**: User description: "Web UI Agent Catalog Administration: spec 4 makes the platform the authority for the agent catalog with Club Admin `/api/v1` operations, but Club Admins have no screen for it. This feature adds a connected Agent Catalog area to the Web UI for Club Admins, consistent with the Registry administration area: browse agents (identifier, kind Coach or Specialist, display name, enabled version, state of each version) and their versions with every artifact reference, version, and digest; view an agent version's artifacts read-only (prompt template text, tool policy as the list of permitted tools, safety policy, model/deployment selection without secrets, and for Coach Agents the disclosure, dimension-set version, base philosophy profile as a readable table of dimensions and integer weights that total 100, and the modifier policy); register new artifact and agent versions from forms with client-side hints but the platform's validation as authority, showing each validation problem next to its field (for example weights not totalling 100 or an unknown tool); approve and retire versions with confirmation; enable a version for the stamp (or change the enabled version) with confirmation and optimistic concurrency, stating plainly that only invocations submitted afterwards use it and existing invocations keep their pinned versions; and see which version the triage conversations will offer. Non-admin members never see the area or its navigation entry; the API remains the authority. Everything goes through application-owned adapters over the generated Kiota client, with the existing navigation, i18n, accessibility, problem handling, and online-only PWA rules; if the OpenAPI snapshot lacks typed schemas for spec 4's operations, the plan adds them without changing behavior. Out of scope: prompt editing assistance or previews rendered by a model, evaluation results, Coach Agent enablement policy beyond what spec 4 allows (spec 9 decides Coach Agent availability), deleting records, new platform operations unless the plan proves a needed read is missing. Depends on `specs/20261018-101517-agent-catalog-registry` (batch sibling), `specs/20261017-185438-web-ui-registry-admin` (#148, pattern and navigation), Web UI Foundation, Installable Web UI with Service Worker Updates (merged). Implements docs/architecture/client-applications.md#web-ui ("platform configuration and administration") for the agent catalog and the operator side of docs/architecture/intelligence-agent-catalog.md (Adding Or Revising A Coach Agent; Prompt And Advice Governance)."

## Clarifications

### Session 2026-10-18

- Q: Should the Web UI offer the enable action for approved Coach Agent versions, which the platform currently refuses? → A: Yes: it offers enabling for every approved version, its confirmation for a Coach Agent version states that the platform may refuse while Coach Agents are not available in conversations, and the platform's refusal is explained; the Web UI does not encode the rule.
- Q: Can a new artifact version start from an existing artifact record of the same kind? → A: Yes: every artifact form can start from an existing record of its kind, prefilled with its content and identifier, and a new version must be entered.
- Q: Should the forms propose a version for the new record? → A: Yes: they propose the next patch version above the highest registered version of the chosen identifier, which the Club Admin can change; a new identifier starts at `0.1.0`.
- Q: How does the area decide what triage conversations offer for an agent? → A: From the platform's read of the agents available for conversations: an agent it lists is offered with the version it reports; every other agent is "not offered", with the reason shown as no enabled version, or for Coach Agents "not available in conversations yet".

## User Scenarios & Testing *(mandatory)*

The actor is the Club Admin, who acts as the catalog operator until a stamp-operator role exists ([Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md), Assumptions). The area is a connected Web UI over the agent catalog operations that [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md) defines; it implements "platform configuration and administration" of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) for the agent catalog and the operator side of [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance) and [Adding Or Revising A Coach Agent](../../docs/architecture/intelligence-agent-catalog.md#adding-or-revising-a-coach-agent). It follows the navigation, page, confirmation, and refusal patterns of the [Web UI Analyst Registry Administration](../20261017-185438-web-ui-registry-admin/spec.md). Every rule the area touches is decided and checked by the platform; the Web UI explains the platform's decisions.

### User Story 1 - See the agent catalog and what conversations offer (Priority: P1)

A Club Admin opens the Agent Catalog area from the navigation and sees every agent of the catalog: its identifier, kind (Coach Agent or Specialist Agent), domain, display name, its enabled version or that none is enabled, and how many of its versions are draft, approved, or retired. For each agent the area states plainly what triage conversations offer, as the platform reports the agents available for conversations: the version members can add and ask, "not offered" for an agent without an enabled version, and "not available in conversations yet" for Coach Agents. The Club Admin opens an agent to see its versions with state, digest, and whether the version is enabled, and its permitted-tool baseline; opens an agent version to see its state, display name, digest, disclosure statement (Coach Agents), every artifact it references (kind, identifier, version, digest), who registered, approved, or retired it and when, whether the approval was a self-approval or acknowledged a differentiation review, and, for Coach Agents, the profile's distance to other approved Coach Agent profiles; and opens each referenced artifact read-only.

**Why this priority**: It replaces raw API calls for the most frequent need, finding out which agent versions the stamp runs and what they consist of, and every other story builds on these views. It changes nothing and is valuable on its own.

**Independent Test**: With the Development and Test seed (the two enabled direct specialists), a Club Admin opens the area, sees all nine catalog agents with the two enabled versions marked as offered in triage conversations, opens `specialist-tactics`, its version, and each of its four artifacts, and sees every listed field, with the prompt text exactly as stored; a Coach sees neither the navigation entry nor the area, and opening any of its addresses shows the not-permitted view.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin, **When** the navigation is shown, **Then** it contains an entry for the Agent Catalog area; **Given** any member without the Club Admin role, **Then** it does not, and opening any Agent Catalog address shows the not-permitted view without loading catalog data.
2. **Given** the Agent Catalog area, **When** it loads, **Then** it lists every agent of the catalog baseline with identifier, kind, domain, display name, enabled version (or "none"), version counts by state, and what triage conversations offer for it.
3. **Given** an agent, **When** the Club Admin opens it, **Then** the view lists its versions with version, state, shortened digest, registration time, and an "enabled" marker, can filter them by state, and shows the agent's permitted-tool baseline.
4. **Given** an agent version, **When** the Club Admin opens it, **Then** the view shows every field listed in this story, with digests shortened and the full values available to read and copy, and links to each referenced artifact.
5. **Given** a prompt template, **When** the Club Admin opens it, **Then** the view shows its group, response contract, declared rendering slots, compatible dimension-set major version (Coach Agents), and its full text exactly as stored, as plain text with line breaks preserved.
6. **Given** a model selection, tool policy, or safety policy, **When** the Club Admin opens it, **Then** the view shows the provider, model, deployment or configuration name, and generation parameters; the permitted tools as a list, each marked whether it lies within the agent's baseline; or the permitted interaction routes, hard boundaries, escalation and refusal categories, and rules, each in words.
7. **Given** a record address that does not exist, **When** the Club Admin opens it, **Then** the not-found view is shown.

---

### User Story 2 - Enable, change, or disable an agent's version for the stamp (Priority: P1)

A Club Admin decides which approved version of an agent the stamp runs. From the agent or one of its approved versions, the Club Admin enables that version, replacing the previously enabled one, or disables the agent, each after a confirmation that names the agent, the current and the new enabled version, and states plainly that only questions submitted afterwards use the new version, while existing invocations, including their retries, keep the version they pinned; disabling states that members can no longer add the agent to conversations or ask it until a version is enabled again. When someone else changed the agent's enablement in the meantime, the Web UI says so and shows the current enablement instead of overwriting it. The platform's refusals (for example a version that is not approved, a retired version, a version whose model selection uses the simulated provider outside Development and Test, or a Coach Agent version while Coach Agent enablement is not supported) are explained in words.

**Why this priority**: Changing which agent version members talk to is the outcome a Club Admin operates the catalog for; it works on the seeded approved versions alone.

**Independent Test**: With a second approved version of `specialist-tactics`, a Club Admin enables it after confirmation, sees the agent's enabled version and the "offered in triage conversations" statement updated, disables and re-enables the agent, and, in a second session that loaded the previous enablement, receives the "enablement changed" explanation instead of an overwrite; an attempt to enable a draft version is explained as a refusal and changes nothing.

**Acceptance Scenarios**:

1. **Given** an approved version that is not enabled, **When** the Club Admin enables it and confirms, **Then** it becomes the agent's enabled version, the confirmation has stated that only questions submitted afterwards use it and existing invocations keep their pinned versions, and the area shows the new enabled version with who changed it and when.
2. **Given** an agent with an enabled version, **When** the Club Admin disables it and confirms, **Then** the agent has no enabled version and the area states that triage conversations no longer offer it.
3. **Given** the confirmation, **When** the Club Admin cancels it, **Then** nothing is sent and nothing changes.
4. **Given** a version the platform refuses to enable, **When** the refusal arrives, **Then** the Web UI explains the reason in words and shows the agent's current enablement.
5. **Given** an approved Coach Agent version, **When** the Club Admin starts enabling it, **Then** the confirmation states that the platform may refuse while Coach Agents are not available in conversations, and a refusal is explained like any other.
6. **Given** the enablement changed since the Club Admin loaded it, **When** the Club Admin confirms a change, **Then** the Web UI says that the enablement has changed, shows the current enablement, and lets the Club Admin decide again; it never repeats the change automatically.

---

### User Story 3 - Register Specialist Agent artifacts and versions (Priority: P2)

A Club Admin registers new artifacts — a prompt template, a model selection, a tool policy, or a safety policy — and a new agent version that binds a catalog agent identifier to exactly one artifact of each required kind, from structured forms. The forms give hints before anything is sent (required fields, allowed choices, length limits, the agent's permitted-tool baseline, the name pattern of model and deployment names, the rendering slots that occur in the prompt text), but the platform decides: when it refuses a record, the form keeps every entered value and shows each problem the platform names next to the field it concerns, and lists problems without a known field above the form. A new agent version can start from an existing version of the same agent, so that only the changed references need choosing. A record that is already registered with the same content is shown with the note that nothing changed; the same identifier and version with different content is explained as immutable.

**Why this priority**: Registration is how a revised prompt or policy reaches the stamp; it builds on the browsing views (Story 1), and its result is enabled through Story 2 after approval (Story 4).

**Independent Test**: A Club Admin registers a new prompt template version for `specialist-tactics`, registers a tool policy naming a tool outside the agent's baseline and sees the problem next to the tool list, registers a valid tool policy, registers agent version `0.2.0` starting from `0.1.0` with the new prompt template, sees it as a draft with its digest, and submits the same prompt template again and sees the "already registered, nothing changed" note.

**Acceptance Scenarios**:

1. **Given** the registration form for an artifact kind, **When** the Club Admin fills it, **Then** the form offers the allowed choices of every enumerated field, shows length limits and the name pattern, and for a tool policy offers only allowlisted tools and marks tools outside the baseline of the agent the Club Admin names as a hint.
2. **Given** a completed form, **When** the Club Admin submits it, **Then** the form shows that registration is in progress, prevents a second submission, and on success opens the stored record with its digest.
3. **Given** the platform refuses the record, **When** the refusal arrives, **Then** the form keeps the entered values and shows every violation the platform names in words next to the field it concerns, and violations without a known field above the form.
4. **Given** an identifier and version that already exist, **When** the Club Admin submits equal content, **Then** the Web UI shows the stored record and says that it was already registered and nothing changed; **When** the content differs, **Then** the Web UI explains that registered versions never change and a new version is needed.
5. **Given** the agent version form, **When** the Club Admin chooses a catalog agent, **Then** the group and the baseline display name come from the catalog, each artifact reference is chosen from registered artifacts of the required kind (prompt templates and safety policies only of the agent's group), and the form can start from an existing version of the same agent.
6. **Given** a registered agent version, **When** it is shown, **Then** its state is draft and it can be approved (Story 4).
7. **Given** an existing artifact record, **When** the Club Admin starts a new version from it, **Then** the form of its kind is prefilled with its content and identifier, and the proposed version is the next patch version above the highest registered version of that identifier, which the Club Admin can change; a new identifier starts with the proposed version `0.1.0`.

---

### User Story 4 - Approve and retire agent versions (Priority: P2)

A Club Admin approves a draft agent version after a confirmation that names the agent, the version, its digest and artifact digests, and states that approved versions never change and that approval records an accountable decision, not evaluation evidence. When the Club Admin registered the version, the confirmation says that the approval is recorded as a self-approval, without blocking it. For a Coach Agent version whose profile is closer than 0.10 to another approved Coach Agent profile, the confirmation lists those distances and requires the Club Admin to acknowledge the differentiation review before the approval can be confirmed. Later the Club Admin retires an approved version that should no longer be enabled, after a confirmation that explains that it can no longer be enabled while invocations that pinned it keep it; the platform refuses to retire the enabled version, and the Web UI explains that another version must be enabled or the agent disabled first.

**Why this priority**: Approval and retirement complete the catalog's governance loop between registration (Story 3) and enablement (Story 2); they work on seeded or registered versions.

**Independent Test**: A Club Admin approves a draft version they registered and sees the self-approval note in the confirmation and the approval with time and member afterwards; tries to retire the enabled version and sees the refusal explained; retires a non-enabled approved version after confirmation; and, in a second session that loaded the draft, receives the "version changed" explanation when approving it again.

**Acceptance Scenarios**:

1. **Given** a draft version, **When** the Club Admin approves it and confirms, **Then** it becomes approved and the view shows who approved it and when; cancelling the confirmation changes nothing.
2. **Given** a Coach Agent version whose profile has a distance below 0.10 to another approved Coach Agent profile, **When** the Club Admin approves it, **Then** the confirmation lists each such distance and cannot be confirmed until the Club Admin acknowledges the differentiation review; the acknowledgement is sent with the approval.
3. **Given** an approved version that is not enabled, **When** the Club Admin retires it and confirms, **Then** it becomes retired and the confirmation has stated that it can no longer be enabled while existing invocations keep it.
4. **Given** the enabled version, a version that is not a draft (for approval), or one that is not approved (for retirement), **When** the platform refuses the change, **Then** the Web UI explains the reason and shows the version's current state.
5. **Given** someone else changed the version since it was loaded, **When** the Club Admin confirms an approval or retirement, **Then** the Web UI says that the version has changed, shows its current state, and lets the Club Admin decide again.

---

### User Story 5 - Read and register Coach Agent philosophy artifacts (Priority: P3)

A Club Admin reads and registers the Coach Agent artifacts: a philosophy dimension set (dimensions with meaning and accepted evidence tools), a base philosophy profile shown and entered as a table with one row per dimension of its dimension-set version (dimension, meaning, integer weight, rationale, provenance), a running total that must reach 100, author and reviewers, and an optional superseded version; a context-modifier policy with its per-dimension bound, total positive movement, and permitted context categories; and Coach Agent versions with their simulation disclosure statement and profile and modifier-policy references. On profile registration and on reads, the Web UI shows the profile's distance to every other approved Coach Agent profile and marks distances below 0.10 for differentiation review.

**Why this priority**: Coach Agents are a later implementation wave and cannot be enabled yet, but their governed artifacts must be operable from the same area so that the later Coach Agent feature only enables them; it builds on Stories 1, 3, and 4.

**Independent Test**: A Club Admin registers dimension set `coach-philosophy-dimensions` 1.0.0, a profile whose weights total 99 and sees both the running-total hint and the platform's violation next to the weights, corrects it to 100 and sees it stored with its distances, registers a modifier policy with a bound of 4 and sees it refused next to the bound, and registers and approves a Coach Agent version with its disclosure statement.

**Acceptance Scenarios**:

1. **Given** a base philosophy profile, **When** the Club Admin opens it, **Then** the view shows its dimension-set identifier and version, a table of every dimension with its meaning, weight, rationale, and provenance, the total of the weights, the author, the architecture and football-domain reviewers, the superseded version if any, and its distances with the ones below 0.10 marked.
2. **Given** the profile form, **When** the Club Admin chooses a registered dimension-set version, **Then** the form offers exactly one row per dimension of that version, accepts whole numbers as weights with a hint for any weight outside 0 to 100, and shows the running total with a hint while it is not 100, without preventing submission.
3. **Given** the platform refuses a profile, modifier policy, dimension set, or Coach Agent version, **When** the refusal arrives, **Then** each violation (for example weights not totalling 100, a missing or unknown dimension, an incompatible dimension-set major version, a bound beyond the allowed range, a context category that is not permitted, or a missing disclosure) is shown in words next to the row or field it concerns.
4. **Given** a Coach Agent version form, **When** the Club Admin fills it, **Then** it requires a disclosure statement within its length limit and offers profiles and modifier policies whose dimension-set major version matches the chosen prompt template, as a hint.
5. **Given** an approved Coach Agent version, **When** it is shown, **Then** the area states that Coach Agents are not offered in conversations yet.

---

### User Story 6 - Use the area safely in every supported setting (Priority: P3)

The Agent Catalog area follows the Web UI's existing rules: it uses the same navigation, language keys, problem handling, and accessibility as the rest of the Web UI, works with keyboard and screen readers, renders all catalog text (prompt text, rationale, provenance, boundaries) as text and never as markup, and is online only, so an offline browser or an installed app without network shows the connection-required state and no catalog data or form content is cached or stored in the browser.

**Why this priority**: These are the Web UI's existing quality rules applied to the new views; they protect access control, accessibility, the prompt text's integrity on screen, and the PWA's no-local-data rule.

**Independent Test**: Automated browser tests reach every Agent Catalog view and action by keyboard, run accessibility rule checks on each view, form, dialog, and error state, check that a prompt containing markup and template placeholders is shown verbatim, check that no catalog response or form content is found in service-worker caches or browser storage, and check that an offline browser shows the connection-required state.

**Acceptance Scenarios**:

1. **Given** any Agent Catalog view, form, dialog, or state (loading, data, empty, refused, error), **When** automated accessibility rule checks run, **Then** they report no violations, and every control is operable by keyboard with visible focus and announced results.
2. **Given** the browser is offline, **When** the Club Admin opens or submits anything in the area, **Then** the connection-required state is shown and nothing is queued for later.
3. **Given** the Club Admin used the area, **When** browser storage and the service-worker caches are inspected, **Then** they contain no catalog responses and no form content.
4. **Given** the Club Admin role is removed while the area is open, **When** the next read or change is made, **Then** the platform's refusal leads to the not-permitted view.

### Edge Cases

- **Empty catalog**: outside Development and Test no seed exists; the area lists the nine catalog agents with no versions and no enabled version, and each artifact list states that it is empty and how it is filled.
- **Unknown values**: a kind, group, state, provider, route, category, rule, slot, violation code, or refusal reason the Web UI does not know is shown with its raw value instead of being hidden or failing.
- **Prompt text with markup or placeholders**: prompt text, rationale, provenance, and boundaries that contain markup, `{{…}}` placeholders, or very long lines are shown verbatim as plain text within bounded space and never interpreted.
- **Long prompts**: prompt text up to the platform's length limit stays readable and scrollable; the form shows the remaining length and the platform refuses oversized text.
- **Agent without versions**: the agent view says that no version is registered and offers registering one.
- **Enabled version of a Coach Agent**: should the platform ever report one, it is shown as enabled; whether conversations offer it follows the platform's read of the agents available for conversations.
- **Concurrent changes**: two Club Admins change the same agent's enablement or the same version's state at once; exactly one change succeeds, and the other sees the "has changed" explanation.
- **Profile distances absent**: a profile without approved Coach Agent profiles to compare with states that no distance applies.
- **Artifact referenced by many versions**: an artifact view lists nothing about other versions beyond what the platform returns; artifacts have no state and are never deleted.
- **Superseded profile**: a profile that names the version it supersedes links to it.
- **Platform busy or failing**: "too many requests", "unavailable", or a server error leads to a recoverable error and changes nothing locally; form content stays in the open form only.
- **Session ends while a form is open**: the Web UI's existing signed-out handling applies; unsaved form content is not kept.
- **Malformed addresses**: an address with an invalid identifier, kind, or version shows the not-found view.

## Requirements *(mandatory)*

### Functional Requirements

#### Navigation and access

- **FR-001**: The Web UI MUST offer an Agent Catalog area with its own navigation entry, shown only to members with the Club Admin role; every Agent Catalog view MUST be addressable, and opening any Agent Catalog address without the Club Admin role MUST show the not-permitted view without loading catalog data.
- **FR-002**: The platform's authorization MUST stay authoritative: a refusal for lack of permission MUST show the not-permitted view, and a missing record MUST show the not-found view.

#### Overview and reads

- **FR-003**: The area's entry view MUST list every agent of the catalog baseline with identifier, kind (Coach Agent or Specialist Agent), domain, display name, enabled version or "none", the number of its versions in each state, and what triage conversations offer for it, as the platform's read of the agents available for conversations reports it: an agent it lists is offered with the version it reports; every other agent is "not offered", with the reason "no enabled version", or for a Coach Agent "not available in conversations yet".
- **FR-004**: An agent's view MUST list its versions with version, state (draft, approved, retired), shortened digest, registration time, and an enabled marker, filterable by state, and MUST show its permitted-tool baseline and its current enablement with who changed it and when.
- **FR-005**: An agent version's view MUST show its state, display name, digest, the simulation disclosure statement for Coach Agents, every artifact reference (kind, identifier, version, digest) linked to the artifact's view, the registering, approving, and retiring members and times where present, whether the approval was a self-approval and whether it acknowledged a differentiation review, whether it is enabled, and, for Coach Agent versions, the profile's distances as the platform reports them.
- **FR-006**: The area MUST list the registered artifacts of each kind (prompt template, model selection, tool policy, safety policy, dimension set, philosophy profile, modifier policy) with identifier, version, shortened digest, and registration time, filterable by identifier, and MUST show each artifact read-only:
  - a prompt template with its group, response contract, declared rendering slots, compatible dimension-set major version for Coach Agents, and its full text exactly as stored;
  - a model selection with provider, model, deployment or configuration name, and generation parameters;
  - a tool policy as the list of permitted tools;
  - a safety policy with its group, permitted interaction routes, hard boundaries, escalation and refusal categories, and rules;
  - a dimension set with each dimension's identifier, meaning, and accepted evidence tools;
  - a base philosophy profile as a table of every dimension of its dimension-set version with meaning, integer weight, rationale, and provenance, the weights' total, author, architecture and football-domain reviewers, superseded version, and distances with every distance below 0.10 marked for differentiation review;
  - a context-modifier policy with its dimension-set major version, per-dimension bound, total positive movement, budget-neutral rule, and permitted context categories.
- **FR-007**: The area MUST show the catalog baseline read-only: the tool allowlist and, per agent, the permitted-tool baseline; when a tool policy is shown in the context of an agent or agent version, each tool MUST be marked whether it lies within that agent's baseline.
- **FR-008**: Digests MUST be displayed in a shortened, recognizable form with the full value available to read and copy, consistent with the Registry administration area.

#### Changes

- **FR-009**: The Club Admin MUST be able to register a record of each artifact kind from a structured form with one control per field of the record's format; enumerated fields MUST offer only the choices the format allows (plus none for optional fields), and the forms MUST offer no field for credentials, endpoints, or other secrets. Every artifact form and the agent version form MUST be able to start from an existing record of the same kind (for agent versions: of the same agent), prefilled with its content and identifier, and MUST propose as version the next patch version above the highest registered version of the chosen identifier (`0.1.0` for a new identifier), which the Club Admin can change.
- **FR-010**: The forms MUST give client-side hints without blocking submission: required fields, length limits and remaining length, the model and deployment name pattern, the tool allowlist and the named agent's baseline, the rendering slots that occur in the prompt text compared with the declared slots, the profile's running weight total compared with 100 and whole-number weights from 0 to 100, and the modifier policy's allowed bounds; the platform's validation MUST stay the authority.
- **FR-011**: The Club Admin MUST be able to register an agent version for a catalog agent identifier, with the group and baseline display name taken from the catalog baseline (the display name editable), one reference of each required artifact kind chosen from registered artifacts (prompt templates and safety policies only of the agent's group; for Coach Agents also a profile and a modifier policy, with those matching the prompt template's dimension-set major version offered first as a hint), and for Coach Agents a disclosure statement.
- **FR-012**: On a registration refusal the form MUST keep every entered value and show each violation the platform names in words, next to the field, row, or list entry its location identifies, and violations without a known location above the form; it MUST translate every violation code the platform defines ([Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md), FR-005 to FR-014) and show unknown codes with their raw value and the platform's detail.
- **FR-013**: A registration of an existing identifier and version with equal content MUST show the stored record with the note that it was already registered and nothing changed; with different content the Web UI MUST explain that registered versions never change and a new version is needed.
- **FR-014**: The Club Admin MUST be able to approve a draft agent version only after a confirmation that names the agent, version, digest, and artifact digests and states that approved versions never change and that approval records an accountable decision, not evaluation evidence; the confirmation MUST state when the approving Club Admin registered the version and that the approval is recorded as a self-approval, without blocking it; for a version with a profile distance below 0.10 it MUST list those distances and require an explicit differentiation-review acknowledgement before it can be confirmed.
- **FR-015**: The Club Admin MUST be able to retire an approved agent version only after a confirmation that states that a retired version can no longer be enabled while invocations that pinned it keep it; a refusal because the version is enabled MUST be explained with the advice to enable another version or disable the agent first.
- **FR-016**: The Club Admin MUST be able to enable an approved version of an agent, replacing the enabled one, and to disable an agent, each only after a confirmation that names the agent and the current and new enabled version and states plainly that only questions submitted afterwards use the change while existing invocations, their retries, and their history keep the versions they pinned; disabling MUST also state that members can no longer add or ask the agent until a version is enabled again. The enable action MUST be offered for every approved version of either group; for a Coach Agent version its confirmation MUST state that the platform may refuse while Coach Agents are not available in conversations. Every enablement refusal the platform defines (version not approved, retired, simulated provider outside Development and Test, Coach Agent enablement not supported, unknown agent) MUST be explained in words.
- **FR-017**: Approvals, retirements, and enablement changes MUST be conditional on the state the Club Admin last saw; when it changed in the meantime, the Web UI MUST say so, show the current state, and require a new decision, and MUST never repeat a change automatically.
- **FR-018**: The Web UI MUST offer no deletion of catalog records, no editing of registered records, no change of the catalog baseline, no model-rendered prompt previews or editing assistance, and no evaluation results.

#### Platform access, client rules, and quality

- **FR-019**: The Web UI MUST reach the platform only through application-owned adapters over the generated client and only through the agent catalog operations of [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md) and the existing member list for member names; it MUST NOT add other platform operations unless the plan shows a needed read is missing. The platform's OpenAPI document MUST describe the request and response bodies these operations use, including each record kind's document, so the generated client has models for them, without changing their behavior.
- **FR-020**: The Agent Catalog area MUST be online only: catalog responses and form content MUST NOT be cached by the service worker or stored in browser storage, changes MUST NOT be queued, and an offline browser or an installed app without network MUST show the connection-required state.
- **FR-021**: All new user-facing text MUST come from translation keys with English as the only shipped language, all new views, forms, and dialogs MUST be operable by keyboard with visible focus, announce results to assistive technology, and pass automated WCAG 2.2 AA rule checks, and all platform-provided text (prompt text, rationale, provenance, boundaries, identifiers, details) MUST be rendered as text, never as markup.
- **FR-022**: Values the Web UI does not know (kinds, groups, states, providers, routes, categories, rules, slots, violation codes, refusal reasons) MUST be shown with their raw value rather than hidden or failing.
- **FR-023**: Automated tests MUST cover the adapters' mapping of agents, versions, enablements, every artifact kind, distances, and the baseline; every violation and refusal explanation; the form hints; the confirmations, their cancellation, the self-approval note, and the differentiation acknowledgement; the "has changed" handling; role-based access; accessibility rule checks; verbatim rendering of prompt text; the online-only rule; and a browser test against the running platform that browses the seeded catalog, registers a new prompt template and agent version of a seeded specialist, approves it, enables it, and sees that triage conversations offer it.

### Key Entities *(include if feature involves data)*

- **Catalog agent**: One of the catalog baseline's agents with identifier, kind (the platform's agent group: Coach Agent or Specialist Agent; not to be confused with an artifact's kind), domain, display name, and permitted-tool baseline, together with its versions and its enablement.
- **Agent version**: An immutable binding of one catalog agent to its artifacts with state (draft, approved, retired), digest, lifecycle members and times, and for Coach Agents a disclosure statement and distances.
- **Catalog artifact**: An immutable, versioned record of one kind (prompt template, model selection, tool policy, safety policy, dimension set, philosophy profile, modifier policy) with digest and registration member and time.
- **Agent enablement**: The stamp's single enabled version of an agent, or none, with who changed it and when; what triage conversations offer follows from it.
- **Catalog refusal**: The platform's named reason for refusing a change, with the violations it lists and their locations.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Club Admin finds which version of an agent triage conversations offer, and that version's prompt text, in at most 3 navigation steps from the Web UI's home view.
- **SC-002**: A Club Admin goes from a revised prompt text to an enabled new agent version of a seeded specialist without any raw API call in at most 10 minutes in a usability walkthrough.
- **SC-003**: In automated tests, every violation code and refusal reason the agent catalog defines is shown with its own worded explanation in 100% of its scenarios, and every violation with a known location appears next to its field.
- **SC-004**: In automated tests, members without the Club Admin role never see the navigation entry and are refused every Agent Catalog view in 100% of cases.
- **SC-005**: Automated accessibility rule checks report zero violations for every Agent Catalog view, form, dialog, and state, and every Agent Catalog action is reachable by keyboard.
- **SC-006**: In automated tests, a concurrent change by another Club Admin is never overwritten: 100% of stale approvals, retirements, and enablement changes end with the "has changed" explanation and no change.
- **SC-007**: In automated tests, no catalog response or form content is found in service-worker caches or browser storage after a session that used the area, and prompt text containing markup is shown verbatim in 100% of cases.

## Assumptions

- The Club Admin role stays the only role that operates the agent catalog until a stamp-operator role exists ([Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md)); read-only catalog views for other roles are out of scope, and what members see when they add agents to conversations stays with the [Web UI Match Triage Conversations](../20261017-210231-web-ui-triage-conversations/spec.md).
- The Web UI uses the agent catalog operations, record formats, violation codes, refusal reasons, and concurrency rules as [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md) defines them (its [API contract](../20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md)); no new or changed platform behavior is expected. Where the committed OpenAPI document lacks typed schemas the generated client needs, the plan adds them without changing paths, statuses, bodies, or rules.
- Coach Agent enablement and availability in conversations are decided by the platform and, later, by Coach Agent Direct Conversations; this area does not encode them and only explains the platform's answers.
- Approval records an accountable decision only; evaluation suites, promotion evidence, and the catalog's Provisional prompt drafts, profile values, and model choices stay as [Validation And Promotion Gates](../../docs/architecture/intelligence-agent-catalog.md#validation-and-promotion-gates) describes them, and the area shows no evaluation results.
- Lists are shown in full as the platform returns them; filtering uses the operations' query parameters and the browser for presentation only.
- Member names are resolved as in the Registry administration area; the seed actor is shown as the development seed.
- The Development and Test seed (approved and enabled `specialist-tactics` and `specialist-video-analysis`, version `0.1.0`) is the starting state of browser tests against the running platform; Coach Agent artifacts are exercised against the simulated platform and the platform's own tests.
- Out of scope: prompt editing assistance and model-rendered previews; evaluation results; Coach Agent enablement policy; deleting or editing records; changing the catalog baseline; offline mode; and new or changed platform operations unless the plan shows a read is missing.
- Dependencies: [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md) (batch sibling; the operations and records), [Web UI Analyst Registry Administration](../20261017-185438-web-ui-registry-admin/spec.md) (#148; the navigation, page, confirmation, refusal, and response-metadata patterns this area reuses), and the merged [Web UI Foundation](../archive/20261012-150657-web-ui-foundation/spec.md) and [Installable Web UI with Service Worker Updates](../archive/20261015-194113-web-ui-pwa-updates/spec.md). The Web UI environment ([Web UI Verification Environment](../archive/20261012-145505-web-ui-environment/spec.md)) already provides Node.js, Kiota, and Playwright; no new framework, SDK, or tool is expected.
- Architecture references: [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) and [Offline Mode](../../docs/architecture/client-applications.md#offline-mode); [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md) (Catalog Rules; Prompt And Advice Governance; Coach Philosophy Weighting; Adding Or Revising A Coach Agent; MCP Tool Policy; Validation And Promotion Gates).
