# Build from the repository root:
#   docker build -f src/platform/SocAlytics.Platform.Migrator/Dockerfile \
#     --build-arg SOCALYTICS_REVISION=<full commit hash> --build-arg SOCALYTICS_VERSION=<YYYY.MMDD.HHMMSS> .

FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510 AS build

ARG SOCALYTICS_REVISION
ARG SOCALYTICS_VERSION

RUN if [ -z "$SOCALYTICS_REVISION" ]; then \
      echo "Build argument SOCALYTICS_REVISION must not be empty." >&2; exit 1; \
    fi; \
    if ! printf '%s' "$SOCALYTICS_VERSION" | grep -Eq '^[0-9]{4}\.[0-9]{4}\.[0-9]{6}$'; then \
      echo "Build argument SOCALYTICS_VERSION must match YYYY.MMDD.HHMMSS (for example 2026.1018.092503)." >&2; exit 1; \
    fi

WORKDIR /src
COPY . .

RUN dotnet publish src/platform/SocAlytics.Platform.Migrator/SocAlytics.Platform.Migrator.csproj \
      -c Release -p:UseAppHost=false -o /app

# The Migrator references ServiceDefaults, which needs the shared framework Microsoft.AspNetCore.App; the plain
# runtime image does not contain it and the Migrator would not start.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374 AS final

ARG SOCALYTICS_REVISION
ARG SOCALYTICS_VERSION

LABEL org.opencontainers.image.revision="${SOCALYTICS_REVISION}" \
      org.opencontainers.image.version="${SOCALYTICS_VERSION}" \
      org.opencontainers.image.source="https://github.com/markusheiliger/socalytics" \
      org.opencontainers.image.title="socalytics-migrator" \
      org.opencontainers.image.description="Applies the pending SocAlytics platform database migrations once and exits."

WORKDIR /app
COPY --from=build /app /app

ENV TMPDIR=/tmp
USER 1654
ENTRYPOINT ["dotnet", "/app/SocAlytics.Platform.Migrator.dll"]
