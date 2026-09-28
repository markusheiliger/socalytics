---
id: strategy
version: 1
operations: [propose, update, apply]
composition: composable
mutation: scoped
isolation: shared
resultSchema: openspec/capabilities/schemas/capability-result-v1.schema.json
---

# Strategy

## Purpose

Define product intent, outcomes, scope, non-goals, priorities, constraints,
stakeholder framing, and observable acceptance criteria.

## Required Inputs

- The selected OpenSpec operation and task, when applicable.
- Current proposal, specifications, design, tasks, and project context.
- Existing accepted behavior affected by the requested decision.

## Authorities

- Accepted behavior remains authoritative in `openspec/specs/`.
- Active changes describe proposed intent and do not override accepted state.

## Permitted Work

- Proposal, scope, outcome, priority, constraint, and acceptance framing
  explicitly assigned by the selected operation or task.
- Behavioral requirements when the selected task explicitly owns them.

## Prohibited Work

- Product code, executable schemas, tests, or implementation configuration.
- Architecture, UX, policy values, or production evidence not approved by the
  selected change.

## Method And Completion

State decisions and unresolved questions explicitly, preserve non-goals, and
trace acceptance criteria to observable outcomes. Complete only when the
requested artifacts are coherent, validation named by the task passes, and no
required decision is silently invented.

## Evidence

Report changed artifacts, decisions, unresolved questions, checks performed,
and the standardized capability result.
