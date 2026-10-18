# Quickstart: Delegated Coach-to-Specialist Consultations

Validation guide for the merged feature. Commands run from the repository root; Docker must be running for the
platform, integration, and image tests. Passing runs are development evidence only; they do not satisfy the Wave 4
evaluation gates.

## 1. Platform: policy, requests, states, outcomes, influence, reads, seed

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AgentOrchestration"
```

Expected: the fixtures of [contracts/consultation-interfaces.md](contracts/consultation-interfaces.md) pass; the
consultation integration tests prove the consultation policy's registration rules, every refusal of
`requestAgentConsultation` with nothing created, idempotency, inheritance and the child's pins, the parent and child
transitions of [data-model.md](data-model.md#invocation-states-parents-with-consultations), restart recovery without
repeated consultations, unavailable consultations with `continue` and `fail`, parent ending and scope loss, the
completion's influence checks, owner reads before and after the parent ended, audit and telemetry content, the
simulated agent workload's `[simulate:consult:…]` behavior, and the consultation seed.

## 2. Hermes: consultation function, waiting, child execution, influence

```powershell
cd src/agents/hermes
uv sync --locked --all-groups
uv run --no-sync pytest
```

Expected: the consultation tests pass with the scripted provider and the fakes: the function is offered only to
consulting Coach Agents, local and platform refusals return to the model, waiting attempts use the waiting pool and
resume with attributed results, children render the consultation section, resumed attempts receive earlier
consultations, influence declarations are requested and validated, and logs contain no consultation content.

## 3. Cross-process evidence

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests --filter "FullyQualifiedName~AgentRuntimeStackTests"
```

Expected: besides the earlier cases, a question to `coach-jurgen-klopp` produces a consultation of
`specialist-tactics` that Hermes executes with a `find_pressing_phases` call through the MCP server, and the parent
succeeds within 120 seconds with the consultation, the child's tool call, and the declared influence in the owner's
reads.

## 4. Web UI

```powershell
cd src/clients/web
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e -- --project platform
```

Expected: the triage views word waiting for consultations and synthesizing, list each consultation below the Coach
Agent advice with attribution, question, response or unavailable reason, evidence, adoption, and agreement (a conflict
marked as disagreement), and open child lineages; the Agent Catalog area shows and registers consultation policies;
against the running platform, the Agent Catalog area shows the seeded consultation policies referenced by the enabled
Coach Agent versions.

## 5. Local run

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

With the AppHost (simulated agent workload), a Coach Agent question that starts with
`[simulate:consult:specialist-tactics]` produces a simulated consultation visible in the conversation. To run Hermes
instead, follow the Hermes README's local command against a platform whose simulated workload is not running.
