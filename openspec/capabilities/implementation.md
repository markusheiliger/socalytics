---
id: implementation
version: 1
operations: [apply]
composition: composable
mutation: scoped
isolation: shared
resultSchema: openspec/capabilities/schemas/capability-result-v1.schema.json
---

# Implementation

## Purpose

Implement approved behavior in the owning component through product code,
tests, executable schemas, migrations, build files, and implementation
configuration.

## Required Inputs

- The exact selected OpenSpec task and every selected capability contract.
- Proposal, specifications, design, tasks, project context, and owning
  component conventions.
- The validated starting branch checkpoint.

## Authorities

- Accepted specifications control observable behavior.
- Approved design and current architecture control implementation boundaries.
- Component-local contracts control executable details.

## Permitted Work

- The smallest coherent implementation, tests, executable contracts, and
  directly related documentation required by the selected task.
- The selected task checkbox after all requested behavior and validation are
  complete.

## Prohibited Work

- Silent changes to product intent, UX, architecture, security policy, or
  accepted requirements.
- Unrelated refactoring, speculative abstractions, or completion of another
  task.
- Independent verification or audit conclusions.

## Method And Completion

Follow established patterns, preserve user changes, surface conflicts, and run
the narrowest relevant validation after substantive edits. Complete only when
the task's requested artifacts, behavior, synchronization, and evidence are
present.

## Evidence

Report changed artifacts, behavior delivered, checks and outcomes, deviations,
remaining risks, and the standardized capability result.
