# OpenSpec Changeset Issue Contract

## Identity

- Title: `OpenSpec changeset: <kebab-case-name>`
- Required label: `openspec:changeset`
- Activation label: `changeset:ready`
- Contract version: `1`

The skill generates the name after final dependency closure. A single-change changeset uses its sole change ref unchanged. A multi-change changeset uses a concise lowercase kebab-case umbrella name derived from the included change refs and proposal outcomes; it must describe shared evidenced scope and must not be a generic label such as `changes`, `changeset`, or `updates`.

## Authoritative Block

The issue body must contain exactly one ordered marker pair. The fenced JSON between the markers is authoritative.

````markdown
<!-- openspec-changeset:v1:start -->
```json
{
  "version": 1,
  "name": "platform-foundation",
  "changes": [
    {
      "ref": "add-platform-persistence-foundation",
      "dependsOn": []
    },
    {
      "ref": "add-club-identity-foundation",
      "dependsOn": [
        "add-platform-persistence-foundation"
      ]
    }
  ]
}
```
<!-- openspec-changeset:v1:end -->
````

Rules:

- Root fields are exactly `version`, `name`, and `changes`.
- `version` is the integer `1`.
- `name` and every `ref` use lowercase kebab case.
- `name` reflects the complete finalized membership and does not depend on selection order.
- `changes` is a non-empty array with unique refs.
- Each change has exactly `ref` and `dependsOn`.
- Every dependency references another change in the same issue.
- Dependencies cannot repeat, refer to self, or form a cycle.
- Archived prerequisites are satisfied and are omitted from the graph.
- Mutable state, task IDs, pull requests, and timestamps never appear in this block.

The controller canonically sorts changes and dependencies before hashing. After the first dispatch, changing this block pauses the changeset until an operator explicitly accepts the new graph.

## Generated Mermaid

The issue contains a Mermaid diagram after the authoritative block. Edges point from prerequisite to dependent. The diagram is always generated with:

```bash
node .github/scripts/openspec-changeset-core.mjs mermaid <changeset-json-file>
```

The controller never parses Mermaid.

## Processing Markers

Controller ledger and summary comments use versioned hidden markers. A cloud-agent pull request body must include:

```html
<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-platform-persistence-foundation"} -->
```

A fully completed pull request also includes:

```html
<!-- openspec-changeset-auto-merge:v1 -->
```

The controller enables squash auto-merge only when both markers are present. The pull request uses `Refs #42`, never `Closes #42`. Only the controller closes the issue after every change is archived on `main`.

A cloud task opens no pull request until the full lifecycle is complete. Work that needs human correction remains committed and pushed on the task branch while the native Agent Task session reports Needs attention. The user responds in that session, and the same task continues on the same branch. If the task becomes terminal and cannot continue natively, an operator may manually dispatch `recover` for the parent issue and change ref; the controller validates the branch checkpoint before starting a replacement session.

## Labels

- `changeset:ready`: validated and available for initial reconciliation.
- `changeset:running`: at least one change is reserved, actively dispatched, or represented by an open pull request.
- `changeset:attention`: graph mutation, native Needs attention, terminal task, failed/expired dispatch, or missing final pull request requires intervention.
- `changeset:complete`: every included change is archived on merged `main`.
