# Research: Deterministic Keyboard Paging Test

## Evidence

Established from the code; the implementation repeats it with runs (FR-001) and records the result in the code comment next to the fix and in the report of its task (the implementation may not change files under `specs/`).

- The test seeds five facts (`pass-0` to `pass-4`, 10 s each) and page size two. The unfiltered first page shows `pass-0` and `pass-1`.
- "Apply range" navigates to `?stream=detections&fromMs=10000&toMs=50000`. The simulated platform returns half-open overlaps (`startMs < toMs`, `endMs > fromMs`): `pass-1` to `pass-4`, so the filtered selection has two pages and "Next page" is enabled once it loads. The selection itself is correct (candidate "no next page" is ruled out).
- `useFactPages` keys the query by selection, so the new selection is pending until its request returns. `FactBrowser` then shows the loading state and unmounts the facts list and the navigation (`view.state === 'loading'`), then mounts new ones. The old and the new controls are different elements (candidate "stale control" is ruled out: the new "Next page" is enabled when it mounts).
- The test waits only for the address and `toHaveCount(2)`. The address changes before React renders the new selection, and the unfiltered page already has two facts, so both waits pass on the previous selection. `activateWithKeyboard` then focuses the old "Next page"; when the filtered request returns (slow under load) the control is detached and the new button, or the loading state, is not the focused one, so `toBeFocused` waits 5 s and fails, also on retry under the same load.

Expected cause (from the code, to be confirmed by the reproduction in T002): the test acts on the previous selection's view (candidates 1 and 2). The page is correct.

## Decisions

- **Decision**: Wait on observable conditions of the filtered page: its first fact is `pass-1` (its `aria-label` ends with `: pass-1`, the unfiltered page starts with `pass-0`) and "Next page" is enabled, then activate it.
  **Rationale**: The wait is on content, so the previous selection's facts never satisfy it; the enabled state is the condition the next action needs. Assertions and accessibility checks stay on the same states.
  **Alternatives considered**: a retry, a sleep, a longer timeout, or `force` (forbidden by FR-004); changing the page to keep the list mounted during reload (changes behavior without a defect; the loading state is the documented contract); changing shared `keyboard.ts` (not at fault, and would affect other tests).
- **Decision**: Keep the existing `await expect(facts).toHaveCount(2)` after the address assertion and insert the new content wait (`facts.first()` `aria-label` ending `: pass-1`) and the `toBeEnabled` wait of "Next page" directly after the `toHaveURL` assertion and before the unchanged `toHaveCount(2)` and the "Next page" activation.
  **Rationale**: No assertion is removed or moved.
- **Decision**: Prove determinism by repeated runs alone, under CPU load, and with the filtered facts request delayed; prove the assertions still bite with two probes (keep "Next page" disabled for the filtered selection; move the focus away), removed afterward.
  **Rationale**: SC-001 and SC-002.
