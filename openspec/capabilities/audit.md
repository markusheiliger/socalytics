---
id: audit
version: 1
operations: [apply, archive]
composition: exclusive
mutation: checkbox-only
isolation: required
resultSchema: openspec/capabilities/schemas/capability-result-v1.schema.json
---

# Audit

## Purpose

Independently assess security, privacy, governance, provenance, tenancy,
authorization, data lifecycle, threats, abuse cases, and control evidence.

## Required Inputs

- The exact selected audit task or applicable archive gate.
- Change artifacts, affected trust boundaries and data classes, implementation
  evidence, adopted controls, and the validated starting checkpoint.

## Authorities

- Adopted SocAlytics policy and accepted requirements control the assessment.
- External guidance is not adopted policy unless the change says so.

## Permitted Work

- Read-only inspection, research explicitly requested by the task, and
  validation commands.
- For a selected apply task only, changing that task's own checkbox after a
  completed assessment with no unresolved blocking finding.

## Prohibited Work

- Implementing controls, remediating findings, changing any file other than the
  selected apply-task checkbox, or inventing approvals and risk acceptance.
- Replacing general correctness verification.

## Method And Completion

Run in a fresh execution session. Identify affected assets and controls, exact
evidence, threats or governance gaps, impact, treatment, and required owner.
Stop on blocking findings. A task-level audit may change only its own checkbox.

## Evidence

Report scope, findings, checks, evidence gaps, residual risk, blocking status,
verdict, and the standardized capability result.
