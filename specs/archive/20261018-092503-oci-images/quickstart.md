# Quickstart: Cloud-Neutral OCI Images

Validation guide for the three images. The interface is in [contracts/container-images.md](contracts/container-images.md);
decisions are in [research.md](research.md).

## Prerequisites

- A running Docker-compatible container runtime with the `docker` CLI (BuildKit builds).
- The .NET SDK pinned by `src/platform/global.json` for the evidence tests.

## Build the images (repository root, PowerShell)

```powershell
$revision = git rev-parse HEAD
$version = (Get-Date).ToUniversalTime().ToString('yyyy.MMdd.HHmmss')
docker build -f src/platform/SocAlytics.Platform.Api/Dockerfile --build-arg SOCALYTICS_REVISION=$revision --build-arg SOCALYTICS_VERSION=$version -t socalytics-api:local .
docker build -f src/platform/SocAlytics.Platform.Migrator/Dockerfile --build-arg SOCALYTICS_REVISION=$revision --build-arg SOCALYTICS_VERSION=$version -t socalytics-migrator:local .
docker build -f src/clients/web/Dockerfile --build-arg SOCALYTICS_REVISION=$revision --build-arg SOCALYTICS_VERSION=$version -t socalytics-web:local .
```

Expected: three images; `docker image inspect socalytics-api:local --format '{{json .Config.Labels}}'` shows the
revision and version; a build without `--build-arg SOCALYTICS_VERSION=…` fails and names the argument.

## Run the evidence

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests
```

Expected: the tests build the three images, then prove that

1. the Migrator applies all migrations to a disposable PostgreSQL, exits `0`, exits `0` again with nothing applied, and
   exits `2` without a connection string;
2. the API container in its production environment reports `/alive` within 30 s and `/health` healthy within 60 s,
   and the API image contains no Migrator or DbUp;
3. the Web UI container serves every shell file byte for byte with matching integrity, the contract headers, caching,
   fallback, and 404 rules, forwards `/api` and `/alive` to the API (including `SocAlytics-Minimum-Web-Version`), and
   answers `502` when the API is unreachable;
4. every container runs as its non-root UID on a read-only root filesystem with only `/tmp` writable, and no image
   declares a health probe;
5. no layer, environment variable, label, or history entry of any image contains secret material, development
   settings, user secrets, environment files, `.git`, or test assets.

The tests fail visibly when no container runtime is available.

## Run a release by hand (illustration only, not a deployment profile)

```powershell
docker network create socalytics-local
# Start PostgreSQL, NATS, and S3-compatible storage on that network, then:
docker run --rm --read-only --tmpfs /tmp --network socalytics-local -e ConnectionStrings__socalytics-migrator=<migrator connection> socalytics-migrator:local
docker run -d --read-only --tmpfs /tmp --network socalytics-local --name api -e ConnectionStrings__socalytics=<app connection> <other API options> socalytics-api:local
docker run -d --read-only --tmpfs /tmp --network socalytics-local -p 8080:8080 -e SOCALYTICS_API_UPSTREAM=http://api:8080 socalytics-web:local
```

The composed stamp with its dependencies is the next feature (Compose Stamp Composition for Self-Hosted Evaluation).
