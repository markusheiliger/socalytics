---
name: soca-changeset
description: Create an issue-backed OpenSpec changeset by selecting active changes, confirming dependencies, and generating a validated dependency graph. Use when the user asks to create a changeset, select OpenSpec changes for processing, or process dependent changes with cloud agents.
allowed-tools: Bash(openspec:*), Bash(gh:*), Bash(node:*)
license: MIT
compatibility: Requires OpenSpec 1.13.0, Node.js 24 or later, GitHub CLI authentication, and a published GitHub repository.
metadata:
  author: SocAlytics
  version: "1.0"
---

Create one GitHub issue that defines a dependency-ordered set of active OpenSpec changes. The issue's marked JSON block is the authoritative graph. Mermaid is generated display only.

Read [references/issue-contract.md](references/issue-contract.md) before constructing the issue. Do not modify generated `opsx-*` prompts or `openspec-*` skills.

## Input

The user may provide change refs, a changeset name, both, or neither. Never infer final membership or dependencies without confirmation.

## Steps

1. **Check prerequisites**

   Run:

   ```bash
   openspec --version
   gh auth status
   openspec list --json
   ```

   Require OpenSpec 1.13.0 or a compatible later version and a GitHub repository with an authenticated `gh` session. If GitHub is unavailable, continue through preview but stop before issue creation.

2. **Discover available changes**

   Parse `openspec list --json`, then run `openspec status --change "<ref>" --json` for each candidate. Include only active changes whose required planning artifacts exist. Show task progress and schema when available.

   Query active changesets:

   ```bash
   gh issue list --state open --label "openspec:changeset" --limit 100 --json number,title,body,url
   ```

   Parse each returned body with `.github/scripts/openspec-changeset-core.mjs`. Mark a change already present in another active changeset as unavailable unless the user explicitly resolves the conflict.

3. **Ask the user to select changes**

   Present eligible changes as a multi-select list. Auto-select only when the user supplied exact refs or exactly one eligible change exists. Ask for a kebab-case changeset name if none was supplied.

4. **Discover and confirm dependencies**

   Read the selected changes' proposal, design, and tasks artifacts. Search for exact active change refs and explicit prerequisite statements. Propose the active transitive prerequisite closure. An archived prerequisite is already satisfied and must not be added to `dependsOn`.

   Show every inferred edge and its source artifact. Ask the user to confirm ambiguous edges and automatic inclusion of active prerequisites. Stop when a required prerequisite is missing, ownership is invalid, or the resulting graph contains a cycle.

5. **Build and validate the graph**

   Construct the strict version 1 JSON object from the issue contract. Write it to a temporary file outside the repository, then run:

   ```bash
   node .github/scripts/openspec-changeset-core.mjs validate <temporary-json-file>
   node .github/scripts/openspec-changeset-core.mjs render <temporary-json-file>
   ```

   Present the final membership, dependency edges, initial runnable frontier, and rendered Mermaid diagram. Ask for final confirmation before any GitHub write.

6. **Create and activate the issue**

   Idempotently ensure these labels exist:

   - `openspec:changeset`
   - `changeset:ready`
   - `changeset:running`
   - `changeset:attention`
   - `changeset:complete`

   Search for an open issue containing the same changeset name before creating another. Create one issue titled `OpenSpec changeset: <name>` using the rendered body and only the `openspec:changeset` label. Add `changeset:ready` in a separate final operation so the controller never sees a partial issue.

   Do not dispatch cloud agents from this skill. The workflow owns dispatch after activation.

7. **Report the result**

   Return the issue number and URL, graph membership, initial runnable frontier, and any satisfied archived prerequisites. If issue creation was unavailable, return the complete rendered body and the exact blocking prerequisite without claiming success.

## Guardrails

- The JSON graph, not Mermaid or prose, controls scheduling.
- Never silently add, remove, or reorder semantic dependencies.
- Never put mutable processing state inside the authoritative JSON block.
- Never place one active change in multiple open changesets without explicit resolution.
- Never close the issue or dispatch an agent directly; the controller owns both actions.
- Product changes continue to follow the full OpenSpec lifecycle.