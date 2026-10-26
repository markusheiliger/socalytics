# Quickstart: Validate the Club Structure Browser Test

Prerequisites: Docker running, Node.js 24, Playwright 1.63.0 Chromium, Kiota 1.36.0 on `PATH`, and `npm ci` done in `src/clients/web`. Run all commands there.

1. **Build and unit tests**: `npm run build` and `npm test` pass.
2. **Alone, repeated** (SC-001): run the dependency projects once with `npm run test:e2e -- --project platform-club-structure --trace on`, then, against the same running AppHost, `npm run test:e2e -- --project platform-club-structure --no-deps --repeat-each 20 --trace on`; all 20 runs of the target pass.
3. **Under CPU load** (SC-001): start one busy-loop process per core, run the project at least 5 times, then stop the processes; every run passes.
4. **After its predecessors and in the whole chain** (SC-001): `npm run test:e2e -- --project platform` at least 5 times; the club structure test passes each time without a rerun (these runs satisfy both measures).
5. **Probes** (SC-002): with each of three temporary probes (team list not refreshed after creation, season not activated, save not persisting the new name) the test fails on its assertions and does not hang; remove the probes afterward.
6. **Other tests of the project** (FR-005): the rest of the `platform-club-structure` project passes unchanged.

Expected outcome: all runs pass without retries, and the code comment next to the fix names the evidenced cause (or states that the failure was not reproduced and which candidates the recorded failures support). See [data-model.md](data-model.md) for the state the test depends on.
