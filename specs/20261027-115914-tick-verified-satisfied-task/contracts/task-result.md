# Contract: Task Work Result (internal, between the work and land jobs)

Component-local contract of the Spec Kit tooling; it is not a product contract and has no JSON Schema file. `speckit-implement-work.mjs` owns it and `speckit-implement.test.mjs` validates it.

## Producer

- `package` (`runPackage`, read-only work job): when the agent's staged change is empty, writes `taskFiles` and writes those paths to `changed-files.txt`; `changedFiles` stays `[]`.
- `verdict` (`runVerdict`): when `patchBytes === 0` and verification is configured, writes `unchanged` (see [data-model.md](../data-model.md)); it does not add failure reasons for the unchanged branch.

## Consumer

`runLand` (write token, never runs agent-written code) trusts only what it recomputes: the patch size, `git rev-parse HEAD`, and the pull request history. It reads `unchanged` only after comparing `unchanged.head` with its own `HEAD`.

## Compatibility

- Results without `unchanged` (older runs, changing tasks, other kinds) behave as today.
- No change to the attempt marker, check run name, labels, or comment markers; only the visible comment text, commit message, and check-run title of an already-satisfied task differ (`already satisfied`).
