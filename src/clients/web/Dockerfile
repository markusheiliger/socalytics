# Build from the repository root:
#   docker build -f src/clients/web/Dockerfile \
#     --build-arg SOCALYTICS_REVISION=<full commit hash> --build-arg SOCALYTICS_VERSION=<YYYY.MMDD.HHMMSS> .

FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510 AS client

ARG SOCALYTICS_REVISION
ARG SOCALYTICS_VERSION

RUN if [ -z "$SOCALYTICS_REVISION" ]; then \
      echo "Build argument SOCALYTICS_REVISION must not be empty." >&2; exit 1; \
    fi; \
    if ! printf '%s' "$SOCALYTICS_VERSION" | grep -Eq '^[0-9]{4}\.[0-9]{4}\.[0-9]{6}$'; then \
      echo "Build argument SOCALYTICS_VERSION must match YYYY.MMDD.HHMMSS (for example 2026.1018.092503)." >&2; exit 1; \
    fi

RUN dotnet tool install Microsoft.OpenApi.Kiota --version 1.36.0 --tool-path /kiota

WORKDIR /src/src/clients/web
COPY src/platform/SocAlytics.Platform.Api/openapi/v1.json /src/src/platform/SocAlytics.Platform.Api/openapi/v1.json

# The arguments are those of the "generate" script in package.json.
RUN /kiota/kiota generate --language typescript \
      --openapi ../../platform/SocAlytics.Platform.Api/openapi/v1.json \
      --output src/generated/platform-api --class-name PlatformApiClient --clean-output

FROM node:24-bookworm-slim@sha256:a5f4366e834695d5f363b920c104953a535d3a9864d1bdf74566a0ebe01daee5 AS build

ARG SOCALYTICS_VERSION

ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1

WORKDIR /src/src/clients/web
COPY src/clients/web/package.json src/clients/web/package-lock.json ./
RUN npm ci

COPY src/clients/web/ ./
COPY --from=client /src/src/clients/web/src/generated ./src/generated

RUN SOCALYTICS_WEB_VERSION="${SOCALYTICS_VERSION}" npm run build:bundle

FROM nginxinc/nginx-unprivileged:alpine@sha256:378d0c32f6562f7fd9629170797f57d2b953aa09be983b3daaf4809bb039ca00 AS final

ARG SOCALYTICS_REVISION
ARG SOCALYTICS_VERSION

LABEL org.opencontainers.image.revision="${SOCALYTICS_REVISION}" \
      org.opencontainers.image.version="${SOCALYTICS_VERSION}" \
      org.opencontainers.image.source="https://github.com/markusheiliger/socalytics" \
      org.opencontainers.image.title="socalytics-web" \
      org.opencontainers.image.description="Serves the SocAlytics Web UI and forwards API requests to the platform API on one origin."

COPY --from=build /src/src/clients/web/dist/ /usr/share/nginx/html/
COPY src/clients/web/container/nginx.conf /etc/nginx/nginx.conf
COPY src/clients/web/container/default.conf.template /etc/nginx/templates/default.conf.template

# A tmpfs at /tmp starts empty, and the template step requires its output directory to exist and be writable.
COPY --chmod=755 <<'EOF' /docker-entrypoint.d/18-prepare-tmp.sh
#!/bin/sh
set -e
mkdir -p /tmp/nginx/conf.d
EOF

ENV NGINX_ENVSUBST_OUTPUT_DIR=/tmp/nginx/conf.d NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 SOCALYTICS_OBJECT_STORAGE_ORIGIN=

EXPOSE 8080
USER 101
