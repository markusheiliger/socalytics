# Architecture Decisions

Architecture decision records (ADRs) are exceptional durable rationale, not a
parallel change workflow or a log of every design choice. During the current
pre-implementation phase, decisions normally update the architecture narrative
that owns the behavior so the documentation describes one coherent current
design.

Add an ADR only when preserving the rationale for a consequential change to an
established or implemented architecture would be useful. Create the ADR in the
same change that updates the affected architecture narratives, so the record
and the current design stay consistent.

An ADR must link to the current architecture narratives it governs, and those
narratives should link back to the ADR.

## Naming

Use a zero-padded sequence followed by a lowercase kebab-case title, such as `0001-example-decision.md`.

## Required Sections

Each ADR must describe:

- Status
- Context
- Decision
- Consequences
- References

The References section must link the current architecture narratives the
decision governs and any related pull request or issue. Keep the ADR focused on
the decision and its rationale rather than a full change history.

Return to the [architecture index](../README.md).
