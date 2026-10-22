# Research: Deterministic Club Structure Browser Test

All items are hypotheses from reading the code path; the implementation confirms or rejects each by reproduction (FR-001). No `NEEDS CLARIFICATION` remains: the cause is a result of the implementation, not a planning input.

## Facts from the code

- `App.tsx` creates one `QueryClient` with defaults (`staleTime` 0, refetch on mount), so a screen that mounts again shows cached data first and refetches in the background.
- `TeamsPage` (`SeasonTeams`): the Season selector value is `chosen` or the first active season; the team list query key is `[...TEAMS_KEY, 'season', seasonId]`; the creation `onSuccess` sets `created` and returns `invalidateQueries(TEAMS_KEY)`.
- `TeamPage`: renders the heading from the cached team and the form only when `!query.isFetching`; the form is keyed by `generation` and keeps its own `name` state; a save writes the result with `setQueryData`; the "Back to teams" button invalidates the non-item team lists in its `onClick`.
- `SeasonPage` invalidates the seasons list (`exact`) after activation; `SeasonsPage` invalidates after creation.
- `club-structure.spec.ts` derives all names from `Date.now().toString(36)` at module load, signs in with the changed password that `session.spec.ts` sets, and archives active seasons in `afterEach` through `archiveActiveSeasons`. The project runs after `platform-navigation` in a chain (`fullyParallel: false`, one worker).
- The earlier spec `specs/archive/20261014-172346-fix-web-ui-keyboard-flake/` already waits for the form to hold the team's current name and lengthened the created-team link timeout; this spec must not repeat it.

## Candidates, ranked

1. **Stale cached team list after Back** (most likely). After the rename the test returns to the list and selects the season; the list can first show the cached list from before the save and refetch afterward. The two positive assertions are ordered so that the absence assertion only runs after `renamedTeam` is visible, which is only sound if the visible list is the refetched one; the order between the invalidation, the remounted `SeasonTeams`, and its own mount refetch (two overlapping fetches of the same key) decides it. Decision: confirm with a delayed list response; fix by observing the refreshed list before asserting absence, in the test, unless the screen shows a state that contradicts its documented behavior.
2. **Created status before the refetch**. `created` is set before the awaited invalidation completes, so "The team was created." says nothing about the list. The test already waits for the link; confirm that the second creation does not race the first one's refetch (the form `name` is cleared on success while the fetch is pending). Decision: observe the condition in the list, as the test does.
3. **Season selector options**. The select appears only after the seasons query resolves, and an option label depends on the state in the cached seasons list (`(Active)` after the activation refetch). `selectOption` retries until the option exists; confirm that it cannot pick the option before the re-fetched state is shown. Decision: wait for the option with the awaited state, which the label already carries.
4. **Shared state between platform specs**. The single active season per club, the changed Club Admin password (session spec), and a session invalidated by another spec. The `afterEach` archives but nothing archives *before* the test, so a season left active by a failure of an earlier project (whose `afterEach` did not run, for example after a crash) makes activation fail. Decision: archive before the test too, only if a trace shows a leftover.
5. **Name collisions**. A base-36 millisecond time is unique per run, but module-level names are shared by the reused worker of a rerun (the rerun is a new attempt of the same module, so the old attempt's season and teams are present with the same names). Decision: a per-attempt suffix from `test.info().retry` or a random part, if a trace shows duplicate names.
6. **Readiness of external dependencies**. Failures of the first sign-in would fail the first assertions of every project, not specifically this test; lowest priority, confirmed only by a trace.

## Method

- Repeat the project alone (at least 20 runs), under CPU load (for example parallel `yes > /dev/null` processes on all cores), and after the other platform projects (`--project platform`), each with `--trace on`; collect the traces of failures and read the failing step and the network order.
- Widen candidate races deterministically with a temporary `page.route` delay on the list and season requests; the one that fails the test without any change to its assertions is the evidenced cause. The delay is removed afterward.
- Alternatives rejected: retries (`retries`, `test.describe.configure`), longer or new timeouts, `waitForTimeout`, skipping or loosening the absence assertion, and `networkidle` waits, because the requester excludes them and they hide the ordering.
