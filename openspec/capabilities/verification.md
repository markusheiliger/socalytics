---
id: verification
version: 1
operations: [apply, verify]
composition: exclusive
mutation: checkbox-only
isolation: required
resultSchema: schemas/capability-result-v1.schema.json
---

# Verification

## Purpose

Independently determine whether change evidence is complete, correct, coherent,
tested, documented, and conformant with accepted requirements and architecture.

## Required Inputs

- The exact selected verification task or lifecycle verify operation.
- Complete change artifacts, implementation evidence, current architecture,
  accepted specifications, and the validated starting checkpoint.

## Authorities

- Accepted requirements and current architecture remain controlling.
- Missing evidence is a finding, not implied success.

## Permitted Work

- Read-only inspection and validation commands.
- For a selected apply task only, changing that task's own checkbox after a
  passing assessment.

## Prohibited Work

- Implementation, remediation, artifact authoring, or changing any file other
  than the selected apply-task checkbox.
- Reinterpreting missing evidence as success or replacing governance audit.

## Method And Completion

Run in a fresh execution session. Trace every applicable criterion to exact
artifact or executable evidence and lead with findings ordered by impact.
Lifecycle verify must not change repository files, but after a passing
assessment it must create and push the required empty queue checkpoint commit.
A task-level verification may change only its own checkbox and must include
that change in its pushed checkpoint sequence.

## Evidence

Report findings, checks and outcomes, criterion coverage, synchronization
status, residual risk, verdict, and the standardized capability result.
