# Quickstart: Validating the Update Feed

Run from the repository root. Docker is needed for the Integration and EndToEnd projects; no step contacts GitHub.

## Prerequisites

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
```

## 1. Options and contract

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "FullyQualifiedName~UpdateFeed"
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --filter "FullyQualifiedName~UpdateOffer"
```

Expected: invalid channel, pin, or cache lifetimes stop startup naming the option; a pin below the minimum names both
options; schema examples and the OpenAPI operation match the contract ([update-offer.md](contracts/update-offer.md)).

## 2. Selection, verification, cache, authorization, metrics

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~UpdateFeed"
```

Expected: stable versus preview (preview offers a newer stable), pin offered exactly or nothing, never below the
minimum, refusal classes counted once per evaluation in `socalytics.registry.update_feed_refusals`, cached verification
not repeated within 15 minutes and failures retried after 1 minute, source failure answered `503` while a cached
verified release keeps being offered, only an active DPoP-authenticated Manager reads the offer, reload changes
channel and pin without restart.

## 3. Manager notice

```bash
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build --filter "FullyQualifiedName~UpdateOffer"
```

Expected: "update available" with the offered version in the snapshot, status window, tray menu, and CLI `status` (plain
and `--json`) only when newer than the build; cleared by a successful "none"; kept on failure; distinct from the minimum
notice; no download, drain, or restart.

## 4. End to end (Linux engine)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~UpdateFeed"
```

Expected: a Manager of an older build shows the notice within one check interval plus 10 seconds against the platform
with the fake release source and none with a current build; existing suites pass unchanged
(`dotnet test src/platform/SocAlytics.Platform.slnx --no-build`).

## 5. Documentation

```bash
node .github/scripts/check-markdown.mjs
```
