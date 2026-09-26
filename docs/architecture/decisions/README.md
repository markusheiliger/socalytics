# Architecture Decisions

Architecture decision records (ADRs) are exceptional durable rationale, not a
parallel change workflow or a log of every design choice. During the current
pre-implementation phase, decisions normally update the architecture narrative
that owns the behavior so the documentation describes one coherent current
design.

Add an ADR only when preserving the rationale for a consequential change to an
established or implemented architecture would be useful. OpenSpec design work
identifies ADR candidates. Once approved, ADR creation is a separate apply task
with exactly `Owner: soca-architect`; neither proposal nor design approval alone
creates an ADR.

Before verification and archive, synchronize the current architecture narrative
and accepted behavioral specifications. An ADR must link to the originating
archived OpenSpec change, affected specifications under `openspec/specs/`, and
the current architecture narratives it governs. The archived change should link
back to the ADR. If the change is not ready to archive, keep the ADR candidate
explicit in the active change rather than creating an untraceable record.

## Records

1. [0001 Shared Persistence Boundary And Database Roles](0001-shared-persistence-boundary-and-database-roles.md)
   establishes the shared platform persistence project as the single owner of
   module-neutral database infrastructure and makes PostgreSQL owner and runtime
   roles the enforcement authority for module schema isolation.

## Naming

Use a zero-padded sequence followed by a lowercase kebab-case title, such as `0001-example-decision.md`.

## Required Sections

Each ADR must describe:

- Status
- Context
- Decision
- Consequences
- References

The References section must contain the OpenSpec change, affected behavioral
specifications, and current architecture narratives. Record unresolved or
rejected alternatives in the originating OpenSpec design rather than expanding
the ADR into a full change history.

Return to the [architecture index](../README.md).
