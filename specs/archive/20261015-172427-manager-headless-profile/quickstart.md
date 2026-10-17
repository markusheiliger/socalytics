# Quickstart: Analyst Manager Headless Server Profile

Validation guide for this feature. Contracts are in
[contracts/headless-manager.md](contracts/headless-manager.md); entities in
[data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK, Docker (for platform tests), Node.js, and the Web UI tooling the
  environment actions install; SoftHSM2 on Linux for the Manager key tests.
- For the manual validation: an Ubuntu Server 22.04 LTS and an Ubuntu Server
  24.04 LTS host (virtual machines are fine) without a graphical environment,
  with a TPM, a virtual TPM, or a stamp that allows software keys for headless
  servers.

## Automated validation

From the repository root:

```powershell
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Then the Web UI unit and browser tests (`npm test` and `npm run test:e2e` in
`src/clients/web`), and `node .github/scripts/check-markdown.mjs`.

Expected:

- The server executable's project and assembly closure contain no Avalonia
  assembly; it refuses to start on Windows and macOS; every CLI command gives
  the same result as in the tray profile.
- The lingering check reports `off` with the hint for a missing marker and
  `on` otherwise; the generated unit matches the contract; `autostart on|off`
  calls `systemctl --user` as specified.
- With `Runtime:Simulated:DaemonAccess=rootful-group`, the headless Manager is
  runtime-unavailable with the account reason under a login shell and admits
  work under a no-login shell; the tray profile is unaffected.
- Pairing records and returns `claimedApplicationProfile`; unknown and
  non-Linux headless claims are refused; the software key policy accepts and
  refuses as in the data model; `all` outside Development and Test fails
  start-up; refusals are audited with profile and protection.
- The Registrar, decision, and fleet views show the claimed profile, and
  `Not reported` for older registrations.

## Manual validation on Ubuntu Server 22.04 and 24.04 LTS

Run on each release after the merge; the person who runs it records the
outcome in the Manager README's Headless Server Profile section, where it is
listed as open until then.

1. Publish the server executable for `linux-x64` and copy it to the host as the
   account that will run it (a dedicated non-login account when the runtime is
   rootful Docker through the `docker` group).
2. Start it without lingering: the warning names `loginctl enable-linger`.
3. As an administrator, run `sudo loginctl enable-linger <user>`.
4. Run `socalytics-manager-server register --platform <stamp URL>`, enter a host
   label, and complete pairing in the Web UI on another device; approve it as
   a Club Admin. The decision view and the fleet show `Headless server
   (claimed)`.
5. Run `socalytics-manager-server autostart on`, reboot, and do not sign in.
   Within 60 seconds after boot, `status` from an SSH session shows the
   Manager Running and connected (SC-001).
6. Run `pause --for 10m`, `resume`, `concurrency 2`, and `status`: the results
   match the tray profile.
7. Run `systemctl --user stop socalytics-analyst-manager.service` while a
   simulated attempt runs: the Manager drains and exits; `systemctl --user
   start` brings it back.
8. Kill the process with `kill -9`: systemd restarts it after about
   10 seconds.
