# Architecture

This directory contains documentation for the overall socAlytics solution and concerns that span multiple components.

Use lowercase kebab-case filenames for architecture narratives. Add each narrative to the index below in its intended reading order with a short description of its purpose.

## Document Index

1. [Architecture Overview](overview.md) introduces the system, its planes, and the status of major decisions.
2. [Terminology and Principles](terminology-and-principles.md) defines the shared concepts and constraints used throughout the architecture.
3. [Tenancy and Technology](tenancy-and-technology.md) describes the deployment stamp model and selected technologies.
4. [Platform Implementation](platform-implementation.md) maps the logical architecture to the provisional implementation baseline.
5. [Contracts and Compatibility](contracts-and-compatibility.md) defines contract authority, representation, and versioning rules.
6. [Client Applications](client-applications.md) describes the web and coach client responsibilities and offline-readiness boundaries.
7. [Analysts, Models, and Hardware](analysts-models-and-hardware.md) defines analyst tiers, preprocessing, provenance, and acceleration strategy.
8. [Match Data Pipeline](match-data-pipeline.md) covers recording ingestion, lineage, and segment materialization.
9. [Job Processing](job-processing.md) defines durable, event-driven analysis workflows and dependency evaluation.
10. [Analyst Manager](analyst-manager.md) describes worker registration, work acquisition, image selection, and outcome reporting.
11. [Analyst Runtime and Recovery](analyst-runtime-and-recovery.md) defines container lifecycle, attempt fencing, credentials, and recovery semantics.
12. [Intelligence and Agents](intelligence-and-agents.md) describes AI access, agent roles, and orchestration authority.
13. [Analyst Capability Catalog](analyst-capability-catalog.md) inventories analyst capabilities, runtime needs, and validation gates.
14. [Intelligence Agent Catalog](intelligence-agent-catalog.md) inventories coach and specialist agents with their constraints.
15. [Client Decision Evidence](client-decision-evidence.md) records evidence and approvals for client architecture decisions.
16. [Security and Data Governance](security-and-data-governance.md) defines the threat model, data classes, lifecycle policies, and production blockers.
17. [Production Operations](production-operations.md) defines deployment, recovery, observability, incident, and readiness expectations.

## Supporting Areas

- [Decisions](decisions/README.md) records architecture decisions and their consequences.
- [Diagrams](diagrams/README.md) contains shared diagram sources and rendered assets.
- [`openspec/specs/`](../../openspec/specs/) is authoritative for accepted behavioral requirements and scenarios.
- [`openspec/changes/`](../../openspec/changes/) contains active change proposals, requirement deltas, designs, and tasks; its `archive/` area preserves completed change history.

Architecture narratives in this directory remain authoritative for the coherent
current system design. OpenSpec specifications describe required behavior rather
than duplicating those narratives. Executable interfaces such as OpenAPI or JSON
Schema belong with the component that implements and validates them once that
component exists.

Return to the [documentation index](../README.md).
