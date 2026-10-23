# Contract: Conversation Purge Event

Machine-readable form: `contracts/agent-orchestration/purge-event/v1/purge-event.schema.json` (JSON Schema 2020-12,
`$id` `https://socalytics.invalid/contracts/agent-orchestration/purge-event/v1/purge-event.schema.json`,
`x-socalytics-version` `1.0.0`, released copy under `releases/1.0.0.schema.json`), owned by Agent Orchestration.

Outbox message type `agent-conversation-purge`, written in the delete transaction:

| Property | Type | Notes |
| --- | --- | --- |
| `schemaVersion` | `"1.0.0"` | required |
| `conversationId` | uuid | required |
| `requestedAt` | date-time | required; the tombstone time |

`additionalProperties: false`. The event carries identifiers only: no `question`, `advice`, `evidence`, or lineage
property is allowed, and the invalid examples (`carries-question`, `carries-advice`, `missing-conversation-id`)
prove it. The purge records, not the message, are the durable purge work queue.
