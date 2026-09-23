# Design: Scaffold Source Areas

## Context

See [proposal.md](proposal.md) for motivation. SocAlytics currently contains governance and architecture documentation but no product source tree. The architecture already describes a provisional polyglot baseline and distinct control-plane, client, agent-runtime, and Analyst boundaries. This change establishes only their first-level repository ownership and promotes the agreed platform baseline without creating executable projects.

The accepted client architecture keeps the Web UI and Electron Coach Client as separate application shells that may share environment-neutral TypeScript packages. The platform profile identifies ASP.NET Core, .NET Aspire, and Docker Compose but currently labels implementation choices provisional.

## Goals / Non-Goals

**Goals:**

- Give every future product component one unambiguous first-level source area.
- Keep the source tree truthful by tracking each area with substantive ownership documentation rather than placeholders.
- Adopt ASP.NET Core on .NET 10 LTS, Aspire local composition, and Docker Compose deployment as the platform baseline.
- Preserve React/TypeScript sharing between the Web UI and Coach Client while making the Web UI release relationship to the platform explicit.
- Synchronize current architecture narratives and repository guidance during apply.

**Non-Goals:**

- Create projects, packages, tests, nested source directories, Dockerfiles, Compose manifests, an Aspire AppHost, or CI.
- Define production topology, secrets, persistence, recovery, capacity, or promotion evidence.
- Change user journeys, API behavior, authentication, authorization, privacy, retention, or Analyst execution contracts.
- Define capability-specific Analyst or agent implementations.

## Decisions

### Use one `src` root with four ownership areas

The immediate children of `src` will be `platform`, `clients`, `agents`, and `analysts`. Each contains only a README in this change. This makes product ownership visible while avoiding speculative project structure.

Alternatives considered:

- Ecosystem roots such as `dotnet`, `typescript`, and `python` were rejected because they obscure deployable and domain ownership.
- Top-level product directories outside `src` were rejected because a single source root gives the repository a stable convention without affecting documentation and OpenSpec roots.
- Empty directories or `.gitkeep` files were rejected because they communicate no ownership and conflict with repository guidance.

### Treat `analysts` as a cross-runtime ownership area

`src/analysts` is the future parent for the .NET Analyst Manager, the Python Analyst SDK, and capability implementations. The planned Manager path is `src/analysts/manager`, but no child is created now.

Keeping Analyst execution concerns together is more important than grouping by language. A separate first-level `analyst-manager` directory was rejected because it fragments one execution-plane ownership area before implementation evidence requires that separation.

### Keep client source ownership separate from platform release composition

The React/TypeScript Web UI remains under `src/clients` with the Electron Coach Client so both can use one future pnpm workspace and share generated API clients, domain-neutral validation, design assets, and environment-neutral agent-access code. Browser authentication, Electron IPC, offline storage, and workflow-specific UI remain application-owned.

The Web UI release is coordinated with the platform API and will be built as a separate container. Future Aspire and Docker Compose configuration may compose that container from the client source area without moving its source under `platform`.

Co-locating the Web UI under `platform` was rejected because runtime composition does not require source co-location and would weaken the accepted client sharing boundary.

### Adopt the platform technology and composition baseline

The platform baseline is ASP.NET Core on .NET 10 LTS. .NET Aspire will provide local composition, service discovery, health checks, OpenTelemetry defaults, and developer dependencies. Docker Compose will deploy the initial platform and its separately containerized Web UI. Detailed production operations remain governed by the existing production-operations narrative and are not approved by this scaffold.

Recording the explicit .NET 10 LTS version avoids a moving "latest LTS" target. Using Aspire as the production orchestrator was rejected because the architecture separates developer composition from deployment. Building Compose files now was rejected because no runnable services exist yet.

### Update current narratives without creating an ADR

`docs/architecture/platform-implementation.md` and `docs/architecture/client-applications.md` will remain the current architecture authority. The platform baseline and Web UI release boundary are ADR candidates because they are durable choices, but the repository is still pre-implementation and its ADR guidance favors updating the owning narrative until a consequential change to established or implemented architecture needs separate rationale.

## Impacts

- **UX:** Unaffected; no client behavior or interface is implemented.
- **Architecture:** Adds source ownership mapping and adopts the platform/client composition decisions described above.
- **Security and governance:** Unaffected; no trust boundary, credential, data lifecycle, or control evidence changes.
- **Implementation:** Adds directories and READMEs only; no executable artifact or dependency is introduced.
- **Documentation:** Updates repository guidance plus the platform and client architecture narratives.

## Risks / Trade-offs

- **[Risk]** First-level areas may be mistaken for implemented components. **Mitigation:** Each README and the root guidance explicitly distinguish planned children from existing artifacts.
- **[Risk]** Coupled Web UI releases may be interpreted as coupled source ownership. **Mitigation:** Architecture documentation separately states source ownership, release coordination, and container boundaries.
- **[Risk]** Docker Compose deployment may appear production-ready. **Mitigation:** Preserve unresolved production topology and evidence in the production-operations narrative and create no deployment manifests in this change.
- **[Trade-off]** A minimal first layer delays executable validation. **Mitigation:** Validate structure, links, OpenSpec consistency, and documentation truthfulness now; add build checks with the first executable component.

## Migration Plan

1. Create the four source-area directories with their ownership READMEs.
2. Update root repository guidance to describe the new non-executable scaffold.
3. Synchronize the platform and client architecture narratives.
4. Validate directory depth, Markdown links, architecture coherence, and OpenSpec state.

Rollback consists of removing the four source-area READMEs and directories and reverting their documentation references. No runtime state or data migration is involved.
