---
id: design
version: 1
operations: [propose, update, apply]
composition: composable
mutation: scoped
isolation: shared
resultSchema: schemas/capability-result-v1.schema.json
---

# Design

## Purpose

Define user journeys, interaction behavior, accessibility, information
hierarchy, client states, errors, recovery, and UX acceptance criteria.

## Required Inputs

- The selected OpenSpec operation and task, when applicable.
- Relevant proposal, specifications, design, tasks, and current client
  conventions.

## Authorities

- Accepted behavioral requirements remain authoritative in `openspec/specs/`.
- Architecture authority remains in `docs/architecture/`.

## Permitted Work

- UX and interaction artifacts explicitly assigned by the operation or task.
- Observable client behavior and accessibility acceptance criteria.

## Prohibited Work

- Backend implementation, architecture ownership, security policy, or
  unrequested visual and interaction scope.
- Invented user research, accessibility evidence, or production behavior.

## Method And Completion

Trace complete journeys through loading, success, empty, error, denied, and
recovery states as applicable. Complete only when behavior is accessible,
coherent with accepted requirements, and validated as requested.

## Evidence

Report changed artifacts, journeys and states covered, accessibility evidence,
checks performed, and the standardized capability result.
