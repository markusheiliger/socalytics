# Quickstart: Validating the Deterministic Invocation Test

Prerequisites: .NET 10 SDK from `src/platform/global.json` and a running Docker-compatible runtime.

1. Restore and build: `dotnet restore src/platform/SocAlytics.Platform.slnx` and
   `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`.
2. Run the test once:
   `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SeedTests"`.
   Expected: all `SeedTests` pass.
3. Repeat the named test 30 times alone (a shell loop with `--filter "FullyQualifiedName~TheSimulatedAgentWorkloadCompletesAnInvocation"`).
   Expected: 30 passes (SC-001).
4. Repeat it 10 times while one busy-loop process per core runs. Expected: 10 passes (SC-001).
5. Run the sibling and helper-sharing tests: `--filter "FullyQualifiedName~AgentOrchestration"`.
   Expected: all pass (SC-002).
6. Probe: temporarily change the expected advice text, then the compared digest, then one artifact kind. Expected: the
   test fails on the matching assertion each time (SC-003). Remove the probes.
7. Reproduce deliberately (before the fix, on the old test): widen the suspected window test-side as recorded in
   [research.md](research.md) `## Evidence`. Expected: the old test fails and the fixed test passes.
8. `node .github/scripts/check-markdown.mjs` for the documentation changes.

Contracts and the data model are unchanged; see [data-model.md](data-model.md). Results are development evidence only.
