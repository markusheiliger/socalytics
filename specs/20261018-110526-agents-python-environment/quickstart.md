# Validation Guide: Agents Python Verification Environment

This is the implementation's validation guide. The behavior below is planned and does not exist merely because this plan was authored.

## Prerequisites

- The existing Linux x64 verification runner, caller-provided Node.js, and `environment-setup` run with `install-tools: true` (for the real-`uv` case and the self-verification).
- Disposable fixture workspaces only; no product project under `src/agents/` and no credentials.

## Environment-Local Tests

From the repository root:

```text
node --test .github/actions/environment-verify/analyst-projects.test.mjs .github/actions/environment-setup/toolchain.test.mjs
```

Required agents-area matrix (every existing Analyst and cross-check case keeps passing unchanged):

| Area | Cases and expected result |
| --- | --- |
| Discovery | No agent project: nothing selected or covered; several `src/agents/<name>` projects recognized and ordered; hidden, ignored, and symbolic-link folders skipped; area-root and nested manifests fail as unsupported layout |
| Convention | Malformed manifest, missing name, `requires-python`, build system, or lock, workspace and non-path sources, and incompatible runtime fail with the agents recommendation |
| Isolation | Agent dependency on an Analyst project fails with its own message; Analyst dependency on an agent project still fails; agent name duplicating an Analyst name fails in the agents area only; `--area analysts` output is unchanged with agent projects present |
| Selection | Own-source change selects the project and transitive consumers once; `contracts/agent-catalog/`, `contracts/agent-tools/`, `contracts/agent-orchestration/`, a new `contracts/agent-x/`, `contracts/common/`, and either environment folder select all; `contracts/agentic/`, `contracts/analysis/`, Analyst, and Manager changes select none; finalize selects all; prefix-similar folders do not match |
| Coverage | Valid agent roots covered; deleted manifest, unsupported manifests, and unowned `src/agents/` files uncovered; `src/agents/README.md` covered only as Markdown |
| Execution | The four stages in order with downloads disabled and the installed interpreter; each failing stage stops the check naming the agent project, stage, command, and one recommendation; missing interpreter or `uv` fails explicitly; dependencies run first |
| Action regression | Check order with the new check between `Analyst Python projects` and `Environment action tests`; Analyst-only and agent-only changes select only their own check; an agent failure keeps the log, `checks`, and `uncovered` outputs; no agent check runs when none exists |

With a real `uv` available, the existing real-fixture case also passes for an isolated agents-area package through all four stages without changing its declarations.

## Repository And Merge Gates

- The environment self-verification of the merge runs the changed action folders in the read-only verification job.
- Validate documentation from the repository root with `node .github/scripts/check-markdown.mjs`.

This feature is held for a person's review before merge because it changes the environment that verifies later implementations. Passing checks are not evidence of MCP Server or Hermes behavior or production readiness.
