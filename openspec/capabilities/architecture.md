---
id: architecture
version: 1
operations: [propose, update, apply]
composition: composable
mutation: scoped
isolation: shared
resultSchema: schemas/capability-result-v1.schema.json
---

# Architecture

## Purpose

Protect and evolve system boundaries, contracts, ownership, tenancy,
authorization architecture, state machines, data lifecycle, and durable design
decisions.

## Required Inputs

- The selected OpenSpec operation and task, when applicable.
- Relevant proposal, specifications, design, tasks, architecture narratives,
  and existing ADRs.

## Authorities

- `docs/architecture/` is authoritative for coherent current system design.
- `openspec/specs/` is authoritative for accepted behavioral requirements.

## Permitted Work

- Architecture narratives, boundary and contract definitions, ADRs, and
  synchronization explicitly assigned by the operation or task.

## Prohibited Work

- Product code, tests, executable schemas, build files, or component
  configuration.
- Invented owners, policy values, thresholds, service objectives, or
  production evidence.

## Method And Completion

Identify the governing invariant, decision owner, durable state, policy, and
validation evidence. Prefer the smallest coherent update establishing one
authority and one unambiguous contract. Complete only when affected narratives,
requirements, and ADR references agree.

## Evidence

Report changed contracts, affected boundaries, ADR disposition, unresolved
decisions, checks performed, and the standardized capability result.
