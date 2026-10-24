# Quickstart: Validating the Deterministic Upload Recovery Test

Prerequisites: Node.js 24, `npm ci` and Playwright Chromium in `src/clients/web`.

```bash
cd src/clients/web
npx vitest run tests/unit/support/fakeObjectStorage.test.ts
npx playwright test --project simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=30
npx playwright test --project simulated tests/e2e/simulated/uploads.spec.ts tests/e2e/simulated/upload-shell.spec.ts
```

Under load (SC-001), run one busy loop per core in the background (`yes > /dev/null &` per core), then `--repeat-each=10`, and stop the loops afterward.

Probes (SC-003, temporary, removed afterward): make the different-file refusal send a part, and make it accept the file; the named test must fail on its assertions and not hang.

Expected: every run passes; no `waitForTimeout` remains in `recovery.spec.ts`; the evidence is in [research.md](research.md); contract: [fake-store-control.md](contracts/fake-store-control.md).
