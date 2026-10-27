# Research: Remove the Remaining Flakiness of the Club Structure Browser Test

All items are hypotheses from reading the code path; the implementation confirms or rejects each by reproduction (FR-001). No `NEEDS CLARIFICATION` remains: the cause is a result of the implementation, not a planning input.

## Facts from the code

- `club-structure.spec.ts` already derives names per attempt (`Date.now()` plus `repeatEachIndex`), archives active seasons through the API in `beforeEach` and `afterEach` (`support/platformState.ts`, which signs in with the changed password of `session.spec.ts`), waits for the form to hold the team's current name, and registers `waitForResponse` for the team list GET before going back, so the absence assertion follows the refreshed list. A comment in the spec records that the earlier flaky cause was not reproduced (20 repeats, a 1.5 s delay on list GETs, CPU load).
- `TeamsPage` (`SeasonTeams`): the Season select shows the first active season unless `chosen`; the team list key is `[...TEAMS_KEY, 'season', seasonId]`; creation `onSuccess` sets `created` and `invalidateQueries(TEAMS_KEY)` (staleTime 0, refetch on mount).
- `TeamPage`: renders the form only when `!query.isFetching`; a save writes the result with `setQueryData`; "Back to teams" invalidates non-item team lists in `onClick`, and the list remounts and refetches on its own.
- The Web UI registers a service worker; on `controllerchange` it reloads the page when the page had a controller (`src/serviceWorker/registration.ts`).
- The e2e project chain runs with `workers: 1` and `fullyParallel: false`; the platform projects share one AppHost, one database, and one Club Admin.

## Candidates still open after the earlier hardening, ranked

1. **Second creation racing the first one's refetch.** `The team was created.` is set before the awaited invalidation finishes and stays visible until the next submit. The second iteration's status assertion can be satisfied by the first iteration's alert if the re-render of `setCreated(false)` has not committed, so the test may continue before the second creation was answered; the later list assertions then race the in-flight creation and refetch (also the later `waitForResponse` can match a GET that predates the rename). Confirm by delaying `POST .../teams`; fix by observing the new team in the list before the next step or by waiting for the creation response.
2. **A list GET matched by `waitForResponse` that does not follow the rename.** The predicate matches any team list GET of any season; a still-pending refetch from the creation step resolving after "Back" is clicked satisfies it with a pre-rename list. Confirm by delaying the earlier list GET; fix by matching on the season id and by registering the wait only after the save was shown.
3. **Reload by the service worker mid-test.** A `controllerchange` after an update check reloads the page and drops in-flight form state. Confirm from the traces (navigation events after sign-in) and the failing step; fix by waiting for the worker to control the page before the test acts, in the test's support, without changing the Web UI.
4. **Season select options and activation refetch.** `selectOption` with the `(Active)` label depends on the refetched seasons list; the select value can change when the seasons list reorders or refreshes after selection. Confirm with a delayed seasons list.
5. **Shared state and platform behavior.** A season left active by a failed project, a changed password, a stale anti-forgery token, a revoked session, or the cleanup's API sign-in interleaving with the browser session; the platform's activation and team creation under concurrent requests or version checks. Confirm from platform logs of failing runs.
6. **Runner load and readiness.** External dependencies of the AppHost in e2e mode; lowest priority, confirmed only by a trace.

## Method

- Repeat the project alone (at least 20 repeats), under CPU load (parallel busy loops on all cores), after `--project platform`, each with `--trace on`; read the failing step and the network order of every failure.
- Widen the candidate races with temporary `page.route` delays in a scratch copy; the one that fails the unchanged assertions is the evidenced cause. If none reproduces, apply the not-reproduced rule of the spec (read the recorded failures of #235, #284, and #314 with `gh run view --log` and `gh run download` where available, and harden only what they support).
- Rejected alternatives: retries, longer or new timeouts, `waitForTimeout`, loosening or skipping the absence assertion, and `networkidle` waits.
