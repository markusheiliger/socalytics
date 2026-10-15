# Quickstart: Analyst Manager Queue Depth and Host Utilization

Validation guide. Contracts are in [contracts/queue-depth.md](contracts/queue-depth.md);
entities in [data-model.md](data-model.md).

## Automated validation

From the repository root (Docker running):

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
node .github/scripts/check-markdown.mjs
```

Expected:

- With five ready jobs for one assigned capability, the operation returns
  `waiting: 5` for it, `0` for the other, total `5`; unknown ids are omitted;
  invalid requests, inactive Managers, and an unreachable broker return the
  documented problems.
- The Manager's state moves through `not-yet-available`, `available`,
  `stale` (after a failed request or two intervals), and `no-capability`;
  admission decisions are the same with and without observations.
- The Linux sampler reports CPU and memory from sample `/proc` files and the
  live host; the status window view model and the CLI show both; the status
  report contains neither.

## Manual validation

1. Run the AppHost (`dotnet run --project src/platform/SocAlytics.Platform.AppHost`)
   and a registered Manager (`dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager`)
   with the simulated runtime, paused.
2. Finalize a recording set so jobs become ready; within one status check
   interval, the status window and `socalytics-manager status` show the
   waiting count and its age.
3. Stop the platform; after two intervals the queue section is marked stale.
4. On Windows and macOS, load the CPU (for example with a busy loop) and
   confirm the CPU value rises within 10 seconds and memory matches the
   operating system's own monitor.
