# Execution Capabilities

OpenSpec specifications under `openspec/specs/` define what SocAlytics does.
The files in this directory define how an agent must conduct and evidence work
for an OpenSpec operation or task. They are repository-owned execution
contracts, not OpenSpec-generated artifacts, agent profiles, personas, or
invokable skills.

Each task declares one unordered, non-empty capability set:

```markdown
- [ ] 2.1 Implement the approved boundary. **Capabilities: architecture, implementation.**
```

An ID resolves directly to `<id>.md` in this directory. IDs are lowercase
single words, and each definition's `id` must match its filename. The supported
IDs are:

- `strategy`
- `design`
- `architecture`
- `implementation`
- `verification`
- `audit`

Every definition follows
[`schemas/capability-v1.schema.json`](schemas/capability-v1.schema.json).
`composition: composable` allows a capability to share one task only with
other mutually compatible composable capabilities. `composition: exclusive`
requires the capability to be the task's only capability. Incompatible
operation, mutation, isolation, or evidence contracts fail before execution;
declaration order never establishes precedence.

Capability-backed apply work returns a final assistant response containing only
one JSON object conforming to
[`schemas/capability-result-v1.schema.json`](schemas/capability-result-v1.schema.json).
The object identifies its trusted contract with
`"schema": "capability-result-v1"`. Schema IDs are controller-recognized
identifiers, never caller-controlled paths or URLs. Prose, Markdown fences,
prefixes, suffixes, arrays, multiple objects, and unknown schema IDs are
rejected. The result is supporting evidence; branch-visible task state,
changed paths, and commit checkpoints remain authoritative.

The OpenSpec CLI does not discover or validate this directory. Repository
tooling validates these contracts and passes their exact paths to the unchanged
OOTB OpenSpec agent in both local and GitHub execution.
