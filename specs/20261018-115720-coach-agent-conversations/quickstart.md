# Quickstart: Coach Agent Direct Conversations

Validation guide for the merged feature. Commands run from the repository root; Docker must be running for the
platform, integration, and image tests. Passing runs are development evidence only.

## 1. Platform: enablement, route, pins, applied profile, disclosure, seed

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AgentOrchestration"
```

Expected: the `work-notification` 1.1.0, `execution-envelope` 1.2.0, and `attempt-report` 1.2.0 fixtures pass; the
Coach Agent integration tests prove enablement, participants, `user-to-coach` submissions with eight pins, the
profile checkpoint and completion checks with every refusal of
[contracts/attempt-report-1.2.0.md](contracts/attempt-report-1.2.0.md), disclosure in every owner read, tool calls of
Coach Agent attempts, the simulated agent workload, and the Coach Agent seed.

## 2. Hermes: validation, rendering, modifiers, completion

```powershell
cd src/agents/hermes
uv sync --locked --all-groups
uv run --no-sync pytest
```

Expected: the Coach Agent tests pass with the scripted provider and the fakes: every validation failure of
[research.md R4](research.md#r4---hermes-artifact-validation-for-coach-agents) ends without model or tool calls, the
profile block contains every element of R5, each modifier rule of R6 applies or omits as specified, and completions
carry the applied profile.

## 3. Cross-process evidence

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests --filter "FullyQualifiedName~AgentRuntimeStackTests"
```

Expected: besides the Specialist Agent case, a question to `coach-pep-guardiola` is claimed by Hermes, calls
`get_match` through the MCP server, applies one `evidence-availability` modifier, and succeeds within 60 seconds with
the disclosure and the applied profile in the owner's reads.

## 4. Web UI

```powershell
cd src/clients/web
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e -- --project platform
```

Expected: Coach Agents appear as a separate participant group with their disclosure, Coach Agent advice shows the
disclosure and, on request, the applied emphasis; the Agent Catalog area enables Coach Agent versions without the
former warning; against the running platform, the Agent Catalog area shows the three seeded Coach Agents enabled and
offered in conversations.

## 5. Local run

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

With the AppHost (simulated agent workload), a Coach of a team with an analysed match can add a seeded Coach Agent to
a triage conversation and receive simulated advice with the disclosure. To run Hermes instead, follow the Hermes
README's local command against a platform whose simulated workload is not running.
