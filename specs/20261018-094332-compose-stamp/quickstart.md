# Quickstart: Compose Evaluation Stamp

Validation guide for the evaluation composition. The interface is in [contracts/compose-stamp.md](contracts/compose-stamp.md);
decisions are in [research.md](research.md). This is not a production profile and passes no production preflight.

## Prerequisites

- Docker Engine with the Compose v2 plugin (`docker compose version`).
- The .NET SDK pinned by `src/platform/global.json` (for the setup console and the evidence).
- The API, Migrator, and Web UI images of one release, built with the commands of
  [the image quickstart](../archive/20261018-092503-oci-images/quickstart.md), for example `socalytics-api:local`,
  `socalytics-migrator:local`, and `socalytics-web:local`.

## Create and start one stamp (repository root, PowerShell)

```powershell
$stampDir = Join-Path $HOME 'socalytics-stamps/club-a'
dotnet run --project src/platform/SocAlytics.Platform.StampSetup -- --output $stampDir
# Fill the empty operator values in $stampDir/stamp.env: image references, release version, port, stamp ID,
# public base URI placeholder, club display name, first Club Admin account name, SMTP relay (or the documented
# placeholder smtp.invalid / 587 / StartTls), and sender.
docker compose -f src/platform/deploy/compose/compose.yaml -p club-a --env-file "$stampDir/stamp.env" up -d
docker compose -f src/platform/deploy/compose/compose.yaml -p club-a --env-file "$stampDir/stamp.env" ps -a
```

Expected: `postgres`, `nats`, and `storage` are healthy; `migrator` exited with `0`; `api` and `web` are running; only
`web` publishes a port, on `127.0.0.1`. Open `http://localhost:<port>` and sign in with the first Club Admin account
name and the initial credential from `stamp.env`; the required password change follows. `/health` stays unhealthy until
the stamp's Registry holds a resolvable default analysis workflow; recording uploads from the browser are unavailable.

## Second stamp and teardown

```powershell
$stampDirB = Join-Path $HOME 'socalytics-stamps/club-b'
dotnet run --project src/platform/SocAlytics.Platform.StampSetup -- --output $stampDirB
# Fill $stampDirB/stamp.env with another port and club, then:
docker compose -f src/platform/deploy/compose/compose.yaml -p club-b --env-file "$stampDirB/stamp.env" up -d
docker compose -f src/platform/deploy/compose/compose.yaml -p club-a --env-file "$stampDir/stamp.env" down --volumes --remove-orphans
```

Expected: `club-b` keeps running; no container, network, or volume of `club-a` remains.

## Run the evidence

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Compose.Tests
```

Expected: the tests build the three images, then prove that

1. the setup console refuses an existing directory and a directory inside a Git working tree, fills every secret, and
   the template defaults no secret; a stamp file with a missing value fails before any container starts and names it;
2. the committed definition publishes exactly one port, on `web` and `127.0.0.1`, pins every infrastructure image by
   digest, and fixes no container, network, or volume name;
3. the Migrator exits `0` before the API starts; the API is alive, unhealthy only for the missing default workflow,
   and healthy within 1 minute after the evidence arranges one; sign-in through the Web UI's origin with the bootstrap
   Club Admin succeeds and requests the password change; a restart keeps the club and applies no migration; a wrong
   migrator password leaves the API uncreated;
4. a second project gets its own club and cannot reach the first stamp's services, and tearing down one stamp leaves
   the other running;
5. after teardown no container, network, or volume of either stamp remains.

The tests fail visibly when Docker or its Compose support is unavailable.
