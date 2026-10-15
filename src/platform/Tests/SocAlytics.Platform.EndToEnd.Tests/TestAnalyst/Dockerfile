FROM python:3-alpine@sha256:618e7b661a89d05565fb402a5e1eab8b1b4e5df1f5ea3dc494998d092ffe1213

ARG MODE=succeed
ARG MANIFEST_SHA256

ENV PYTHONDONTWRITEBYTECODE=1

LABEL io.socalytics.analyst-manifest.version="1.0.0" \
      io.socalytics.analyst-manifest.sha256="${MANIFEST_SHA256}" \
      io.socalytics.capability.id="e2e-docker-analyst" \
      io.socalytics.capability.version="1.0.0" \
      io.socalytics.model.digest="sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

COPY analyst.py /opt/analyst/analyst.py
RUN echo "${MODE}" > /opt/analyst/mode

ENTRYPOINT ["python", "/opt/analyst/analyst.py"]

# The manifest must be the only file of the last layer.
COPY analyst-manifest.json /opt/socalytics/analyst-manifest.json
