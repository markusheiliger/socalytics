# Data Model: Deterministic Club Structure Browser Test

The feature adds no persisted entity, table, or contract. These are the pieces of state the test depends on and that the fix may have to isolate.

| State | Owner | Rule the test relies on |
| --- | --- | --- |
| Club Admin account `club-admin` | platform instance of the chained platform projects | Its password is the one `session.spec.ts` sets (`E2e-Changed-Password-0002!`); the project runs after `platform-session`. |
| Active season | club (at most one) | None is active when the test starts; none is active when it ends, even on failure (`afterEach` through `archiveActiveSeasons`). |
| Season `E2E structure <run>` | created by the test | Draft after creation, `Active` after activation, archived by the cleanup. |
| Teams `E2E first/second/renamed <run>` | created by the test | The second team is renamed; its old name is absent from the refreshed list. |
| Run identifier `<run>` | test | Unique per attempt, so a rerun does not see the data of the previous attempt. |
| Client query cache (`seasons`, `teams` lists, `teams item`) | Web UI | Stale-while-revalidate; a remounted list shows cached data before its refetch resolves. |

State transition under test: `no season → Draft season → Active season → two teams → second team renamed → list shows renamed and first team, not the second`.
