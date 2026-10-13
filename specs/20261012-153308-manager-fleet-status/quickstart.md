# Quickstart: Validate the Status Report and Fleet Read API

Development evidence only.

## Prerequisites

- .NET SDK from `src/platform/global.json` (also pinned by the Manager), and
  Docker for PostgreSQL.
- Linux with SoftHSM2 for the Manager's PKCS#11 tests.
- Analyst Manager Work Execution merged (attempt registry and concurrency
  limit).

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

| Test area | Proves |
| --- | --- |
| Integration `Registry/StatusReportTests` | Report storage and overwrite, refusal of invalid reports without blocking the check, the interval in responses, deletion on revocation and unregistration completion |
| Integration `Registry/FleetReadTests` | Club Admin fleet with status and connectivity (online, offline after missed intervals, not applicable), the Registrar view of own submissions only, `403`/`404` rules |
| Integration `Registry/HostLabelTests` | Control-character refusal and the label in the submission response |
| Contracts and Manager `GoldenFixtureTests` | `contracts/analyst-manager/status/v1/` |
| Manager `Registration/StatusReportingTests` | Report content from operating state, preflight, attempts, and limit; the platform interval is used; refusals are logged; local label validation |
