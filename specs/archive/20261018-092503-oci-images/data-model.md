# Data Model: Cloud-Neutral OCI Images for the Platform API, Migrator, and Web UI

This feature stores no data and adds no migration or table. The entities below are build and runtime descriptors whose
fields and rules the image definitions and the evidence enforce; the full interface is in
[contracts/container-images.md](contracts/container-images.md).

## Deployable image

| Field | Rule |
| --- | --- |
| Kind | `api`, `migrator`, or `web` |
| Image definition | one Dockerfile and one Dockerfile-specific ignore file per kind |
| Base images | every stage pinned by digest; no CPU architecture named |
| Source revision | required build input; non-empty; written to `org.opencontainers.image.revision` |
| Release version | required build input; shape `YYYY.MMDD.HHMMSS`; written to `org.opencontainers.image.version` |
| User | fixed non-root numeric UID (1654 for `api` and `migrator`, 101 for `web`) |
| Port | 8080 for `api` and `web`; none for `migrator` |
| Writable location | `/tmp` only |
| Health probe | none declared |
| Forbidden content | secrets, private keys, development certificates, user secrets, `appsettings.Development.json`, `.env*`, `.git`, test assemblies and fixtures; for `api` additionally the Migrator and DbUp; for `migrator` additionally the API host; for `web` additionally build tooling and `node_modules` |

## Release

| Field | Rule |
| --- | --- |
| Images | exactly one image of each kind |
| Revision and version | identical across the three images |
| Coupling | the `web` image's shell reports the release version; the API's `WebUi:MinimumVersion` compares against it |

## One-origin forwarding contract

| Field | Rule |
| --- | --- |
| Paths | `/api`, `/api/**`, `/alive` |
| Upstream | `SOCALYTICS_API_UPSTREAM`, resolved per request |
| Added headers | `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host` |
| Limits | no body limit, no buffering, one-hour read and send timeouts |
| Failure | `502` for forwarded paths while static serving continues |

## Lifecycle

```text
Migrator container: created → running → exited(outcome code)   (never restarted after exit code 0)
API container:      created → running (alive) → ready (/health healthy) ⇄ not ready
Web UI container:   created → running (serves shell; forwards API paths or answers 502)
```
