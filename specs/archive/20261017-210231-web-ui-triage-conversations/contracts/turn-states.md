# Contract: Turn States and Messages

Every turn and view state has its own worded message from the `triage` translation namespace. The pure functions
`turnPresentation(turn)` and `triageMessage(outcome)` derive them; components only render.

## Turn presentation

| Platform state | Presentation | Polling |
| --- | --- | --- |
| (sending, no answer yet) | "Sending…" with the question | — |
| unknown outcome after a network failure | "We could not confirm that your question was received." with "Send again" (same key) | — |
| `accepted/queued` | "Waiting for the agent" (pending) | yes |
| `claimed/running` | "The agent is working on it" (pending), attempt number when above 1 | yes |
| `succeeded` | Advice card: agent name and identifier, AI disclosure, advice text, evidence references or "No evidence references were recorded for this advice", "Show lineage" | stops |
| `failed`, `attempts-exhausted` | "No advice: the agent did not finish after several attempts." | stops |
| `failed`, `execution-failed` | "No advice: the agent could not answer this question." | stops |
| `failed`, `scope-lost` | "No advice: access to this team was lost while the question was being answered." | stops |
| `failed`, other reason | "No advice" with the raw reason | stops |
| unknown state | the raw state | continues until terminal or `404` |

State changes are announced through a polite live region ("Advice from Tactics specialist is ready").

## View messages

| Situation | Message |
| --- | --- |
| no own conversations for the match | "You have no triage conversations for this match yet." |
| no eligible run (Editor) | "Triage needs a finished analysis of this match first." |
| run superseded | "This conversation stays bound to an analysis run that is no longer the match's current run." |
| Viewer or `403` on submit | "You can read this conversation, but only Club Admins and Coaches of the team can ask questions." |
| all enabled agents participate | "All available agents already take part in this conversation." |
| `tooManyUnfinished` | "Too many of your questions are still being answered. Send this one when one of them has finished." |
| `agentNotAvailable` | "This agent is no longer available on this platform." |
| `agentNotParticipant` | "Add this agent to the conversation before asking it." |
| `participantExists` / `staleVersion` | "The conversation changed. It has been reloaded; choose again." |
| `runNotEligible` | "This analysis run cannot be used for triage." |
| `invalidQuestion` | field message next to the question (required, too long, other) |
| `notFound` | the not-found view |
| offline | the connection-required state |
| leaving with unsent input | "Leave this conversation? Your unsent question will be lost." |

## Lineage entry kinds

`submitted`, `claimed`, `checkpoint`, `lease-expired`, `deadline-reached`, `failed`, `scope-lost`, `exhausted`, and
`completed` are worded; other kinds show the raw kind. Each entry shows its attempt number when present, its time, and
its details as text; the accepted advice's execution references are listed by name.
