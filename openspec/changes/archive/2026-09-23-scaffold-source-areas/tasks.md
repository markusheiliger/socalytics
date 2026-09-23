# Tasks

## 1. Source-Area Scaffold

- [x] 1.1 Create `src/platform/README.md`, `src/clients/README.md`, `src/agents/README.md`, and `src/analysts/README.md` with purpose, planned ownership, exclusions, and authoritative relative links; verify `src` has exactly those four immediate directories and that each contains only its README. Owner: soca-developer

## 2. Architecture And Repository Synchronization

- [x] 2.1 Update `docs/architecture/platform-implementation.md` to adopt the first-level source mapping, ASP.NET Core on .NET 10 LTS, Aspire local composition/service defaults, Docker Compose deployment, and separate API/Web UI containers while preserving unresolved production-operation details; verify the narrative does not claim executable projects or deployment evidence exist. Owner: soca-architect
- [x] 2.2 Update `docs/architecture/client-applications.md` to record client source ownership, Web UI/platform release coordination, separate containerization, Coach Client release independence, and environment-neutral sharing limits; verify it remains coherent with the platform implementation narrative. Owner: soca-architect
- [x] 2.3 Update `README.md` and `AGENTS.md` to show the actual first-level source scaffold and retain truthful no-build/no-test guidance; verify every listed path exists and no executable command is documented as available. Owner: soca-architect

## 3. Verification Remediation

- [x] 3.1 Normalize the four source-area READMEs so Markdown diagnostics, including the single-trailing-newline rule, pass without changing their approved ownership content; verify all four files have no editor diagnostics and their relative links still resolve. Owner: soca-developer
- [x] 3.2 Normalize `proposal.md`, `specs/repository-foundation/spec.md`, and `tasks.md` so Markdown heading, spacing, list, and trailing-newline diagnostics pass without changing intent, requirements, scenarios, ownership, or completion state; verify OpenSpec validation and editor diagnostics both pass. Owner: soca-strategist
- [x] 3.3 Normalize `design.md` so Markdown heading and trailing-newline diagnostics pass without changing approved decisions or scope; verify editor diagnostics and relative links pass. Owner: soca-architect
- [x] 3.4 Replace the invalid repository-wide `openspec status --json` guidance in `README.md` with `openspec status --all --json`; verify the documented command exits successfully and README diagnostics and links remain clean. Owner: soca-architect
- [x] 3.5 Replace the invalid repository-wide `openspec status --json` guidance in `AGENTS.md` with `openspec status --all --json`; verify the documented command exits successfully and AGENTS diagnostics and links remain clean. Owner: soca-architect

## 4. Independent Verification

- [x] 4.1 Independently verify the `repository-foundation` scenarios, source depth and contents, Markdown diagnostics and relative links, cross-document architecture coherence, and the absence of excluded executable artifacts; then run `openspec doctor --json`, `openspec schema validate spec-driven --json`, `openspec validate --all --json`, and `openspec status --all --json` and record any blocking findings before completion. Owner: soca-verifier
