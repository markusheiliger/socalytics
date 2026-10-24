# Build from the repository root (the ARG defaults are the pins and must not be overridden for registered images):
#   docker build -f src/analysts/capabilities/media-context-classification/Dockerfile -t media-context-classification:local .
#
# The uv and FFmpeg pins equal .github/actions/environment-setup/analyst-media-toolchain.json.
# The base image digest of python:3.12-slim-bookworm was current on 2026-10-22.

FROM python:3.12-slim-bookworm@sha256:6a0e06f5ba0b373d051aabd3fffb9b873f27f9a2a3a14bcbe37dee6f0e3bbc70 AS build

ARG UV_URL=https://github.com/astral-sh/uv/releases/download/0.13.1/uv-x86_64-unknown-linux-gnu.tar.gz
ARG UV_SHA256=eadd9d27c134ab5ac1ffd4473b97a741407f3ea238288f03121fc4deddf90220
ARG UV_ROOT=uv-x86_64-unknown-linux-gnu
ARG FFMPEG_URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/ffmpeg-n8.1.3-9-g29e619e767-linux64-lgpl-8.1.tar.xz
ARG FFMPEG_SHA256=dfa863a00ca81f1bdf58a372b18cff4820f0017e55de32778de8ecd8ed92a02e
ARG FFMPEG_ROOT=ffmpeg-n8.1.3-9-g29e619e767-linux64-lgpl-8.1
ARG MANIFEST_SHA256=df44f25ec7e04c9259c973dbfe05e8c05f5af62f8b9a3e14ee08c0f9106e9b7c

ENV UV_PYTHON_DOWNLOADS=never \
    UV_PYTHON=/usr/local/bin/python3.12 \
    UV_PROJECT_ENVIRONMENT=/opt/media-context-classification/venv

WORKDIR /build
COPY src/analysts/capabilities/media-context-classification/analyst-manifest.json /tmp/analyst-manifest.json
COPY src/analysts/capabilities/media-context-classification/docker/fetch_verified.py /tmp/fetch_verified.py

RUN echo "${MANIFEST_SHA256}  /tmp/analyst-manifest.json" | sha256sum -c - \
    || { echo "analyst-manifest.json does not match the literal io.socalytics.analyst-manifest.sha256 value; update the Dockerfile label and MANIFEST_SHA256." >&2; exit 1; }

RUN python3.12 /tmp/fetch_verified.py --url "${UV_URL}" --sha256 "${UV_SHA256}" --dest /opt/tools \
      --member "${UV_ROOT}/uv" \
    && python3.12 /tmp/fetch_verified.py --url "${FFMPEG_URL}" --sha256 "${FFMPEG_SHA256}" \
      --dest /opt/media-context-classification/bin \
      --member "${FFMPEG_ROOT}/bin/ffmpeg" --member "${FFMPEG_ROOT}/bin/ffprobe" --member "${FFMPEG_ROOT}/LICENSE.txt" \
    && chmod 0755 /opt/tools/uv /opt/media-context-classification/bin/ffmpeg /opt/media-context-classification/bin/ffprobe \
    && chmod 0644 /opt/media-context-classification/bin/LICENSE.txt

COPY src/analysts/sdk/ src/analysts/sdk/
COPY src/analysts/capabilities/media-context-classification/ src/analysts/capabilities/media-context-classification/

WORKDIR /build/src/analysts/capabilities/media-context-classification
RUN /opt/tools/uv sync --locked --all-groups --no-install-local --no-build \
    && /opt/tools/uv sync --locked --no-dev --no-editable --no-build-isolation --compile-bytecode \
    && chmod -R a+rX,go-w /opt/media-context-classification

FROM python:3.12-slim-bookworm@sha256:6a0e06f5ba0b373d051aabd3fffb9b873f27f9a2a3a14bcbe37dee6f0e3bbc70 AS final

LABEL io.socalytics.analyst-manifest.version="1.0.0" \
      io.socalytics.analyst-manifest.sha256="df44f25ec7e04c9259c973dbfe05e8c05f5af62f8b9a3e14ee08c0f9106e9b7c" \
      io.socalytics.capability.id="media-context-classification" \
      io.socalytics.capability.version="1.0.0" \
      io.socalytics.model.digest="sha-256:0000000000000000000000000000000000000000000000000000000000000000"

COPY --from=build --chown=0:0 /opt/media-context-classification /opt/media-context-classification

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PATH=/opt/media-context-classification/bin:/usr/local/bin:/usr/bin:/bin

USER 65532:65532
ENTRYPOINT ["/opt/media-context-classification/venv/bin/python", "-m", "media_context_classification"]

COPY src/analysts/capabilities/media-context-classification/analyst-manifest.json /opt/socalytics/analyst-manifest.json
