# Research: Merge Verification Time Limit

## Decision 1: Narrow the self-test with an action input

- **Decision**: add the optional input `environment-tests-only` (default `false`) to `environment-verify`. When `true`,
  the script skips the verification plan, all components, the Python projects, and the Markdown check, and runs only
  "Environment action tests". It fails (with one recommended action) when the tests are not configured, so a
  self-test never passes without running anything, except in a nested run, which the marker deliberately skips.
- **Rationale**: the self-test must exercise the changed action from the workspace; only the action can narrow itself
  without a second copy of its logic in the workflow. The action's own tests already include the fixtures that run
  the action script with stubs, so the changed script is exercised. The preceding full verification runs every
  component test, but with the trusted action from the tooling checkout, not the changed one. The changed action's
  component, plan, and Markdown logic is therefore covered by the action's own fixture tests (stubbed tools), not by
  a repeated real run on the merged tree; the fixtures must exercise the changed script for every component kind
  (plan, .NET, Node, Python, Markdown) so a regression still fails the self-test.
- **Alternatives considered**:
  - A new `mode` value: `mode` also feeds the verification plan, which would need a mode it does not know.
  - Run `node --test` directly in a workflow step: bypasses the action's logging, outputs, and nested-run guard, and
    would not load the changed action script.
  - Keep the repeat and only raise the timeout: doubles the cost of every environment merge.

## Decision 2: Time limit 150 minutes

- **Decision**: `timeout-minutes: 150` for the job `verify`.
- **Rationale**: the estimate per step is: full verification about 70 minutes (measured in the four cancelled runs),
  environment-setup self-test about 5 minutes, narrowed verification self-test about 2 minutes, setup and integrate
  about 3 minutes (these three are estimates, not measurements), about 80 minutes in total. The 150-minute limit is
  derived from that sum plus margin: one third of margin is about 107 minutes; 150 leaves a margin of almost 90% and
  stays below the 360-minute cap. SC-002 (the self-test adds less than 10%) is an observed outcome checked after the
  first environment merge (quickstart step 5), not a gate. If the self-test had still repeated the full verification (about
  150 minutes), 150 would not have sufficed, so both changes are needed together.
- **Alternatives considered**: 360 (the cap) hides a hung job for six hours; 120 leaves a margin of only 50% over
  the measured total and little for a slower runner or a flaky-test rerun.

## Decision 3: Documentation

- **Decision**: the workflows README and the environment-verify README do not state the 90-minute limit today, so
  they gain the new limit and the self-test scope where they describe the merge verification and the action; the
  environment-verify README gets the new input and corrects "the merge self-tests".
- **Rationale**: documentation must describe the implemented state (FR-007).

## Decision 4: Nested runs

- **Decision**: keep `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING`. The fixtures of the environment tests that run the action
  never recurse into the self-test.

All Technical Context items are resolved; no NEEDS CLARIFICATION remains.
