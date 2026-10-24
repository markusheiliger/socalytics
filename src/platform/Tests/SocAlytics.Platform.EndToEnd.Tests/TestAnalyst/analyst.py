import hashlib
import json
import os
import re
import sys
import time
import uuid

WORKFLOW_ID = "e2e-docker-workflow"
WORKFLOW_VERSION = "1"
# Keeps the container running long enough for the test to inspect it.
HOLD_SECONDS = 4
RESULT = "/socalytics/result"
MEDIA = "/socalytics/media"


def read_mode():
    with open("/opt/analyst/mode") as f:
        return f.read().strip()


def attempt_id():
    # job.json carries no attempt identity; the host attempt directory name in the mount table does.
    with open("/proc/self/mountinfo") as f:
        for line in f:
            match = re.search(r"socalytics-analyst-attempts/([0-9a-f]{32})", line)
            if match:
                return str(uuid.UUID(match.group(1)))
    raise SystemExit("the attempt directory is not visible in the mount table")


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            digest.update(chunk)
    return "sha-256:" + digest.hexdigest()


def read_media(job):
    """Verifies the staged segment media and returns one fact inside the nominal window."""
    manifest_path = f"{MEDIA}/segment-manifest.json"
    media_path = f"{MEDIA}/segment.mp4"
    if sha256_file(manifest_path) != job["segmentMedia"]["manifestChecksum"]:
        raise SystemExit("the segment manifest does not match the job")
    with open(manifest_path) as f:
        manifest = json.load(f)
    if sha256_file(media_path) != manifest["media"]["digest"] or os.path.getsize(media_path) != manifest["media"]["sizeBytes"]:
        raise SystemExit("the segment media does not match its manifest")
    window = job["segmentWindow"]
    fact = {
        "kind": "e2e-media-observation",
        "startMs": window["startMs"],
        "endMs": window["startMs"] + 1000,
        "confidence": 1,
        "payload": {"mediaDigest": manifest["media"]["digest"], "sizeBytes": manifest["media"]["sizeBytes"]},
    }
    return {
        "facts": {
            "contractVersion": "1.0.0",
            "capability": job["capability"],
            "schemaVersion": job["schemaVersion"],
            "outputType": "foundational-facts",
            "facts": [fact],
        }
    }


def main():
    mode = read_mode()
    with open("/socalytics/job.json") as f:
        job = json.load(f)

    if mode == "oom":
        # Allocates and touches memory until the container's limit kills it.
        chunks = []
        while True:
            chunks.append(b"x" * (32 * 1024 * 1024))

    time.sleep(3600 if mode == "hang" else HOLD_SECONDS)
    if mode == "fail":
        sys.exit(3)
    if mode == "no-manifest":
        return

    media_facts = read_media(job) if mode in ("media", "payload-invalid") else None
    if media_facts is not None:
        artifact_name, media_type = "facts.json", "application/vnd.socalytics.facts+json"
        artifact = json.dumps(media_facts["facts"], sort_keys=True).encode()
    else:
        artifact_name, media_type = "summary.json", "application/json"
        artifact = json.dumps({"logicalJobId": job["logicalJobId"], "ok": True}, sort_keys=True).encode()
    with open(f"{RESULT}/{artifact_name}", "wb") as f:
        f.write(artifact)

    pinned = job["pinned"]
    provenance = {
        "capability": job["capability"],
        "schemaVersion": job["schemaVersion"],
        "analystProfile": {"id": pinned["analystProfileId"], "digest": pinned["analystProfileDigest"]},
        "imageDigest": pinned["imageDigest"],
        "policyVersions": [{"id": p["policyId"], "version": p["version"]} for p in pinned["policies"]],
        "workflow": {"id": WORKFLOW_ID, "version": WORKFLOW_VERSION},
    }
    if pinned.get("modelDigest"):
        provenance["modelDigest"] = pinned["modelDigest"]

    manifest = {
        "contractVersion": "1.0.0",
        "scope": {
            "teamId": job["teamId"],
            "matchId": job["matchId"],
            "recordingSetVersionId": job["recordingSetVersionId"],
            "runId": job["runId"],
            "nodeId": job["nodeId"],
            "logicalJobId": job["logicalJobId"],
            "match": True,
        },
        "provenance": provenance,
        "attempt": {"attemptId": attempt_id(), "attemptNumber": 1},
        "artifacts": [
            {
                "name": artifact_name,
                "digest": "sha-256:" + hashlib.sha256(artifact).hexdigest(),
                "sizeBytes": len(artifact),
                "mediaType": media_type,
            }
        ],
        "validityWindow": {"startMs": 0, "endMs": 1000},
        "factCount": 0,
        "upstreamLineage": [],
        "outputType": "soccer-concepts",
        "confidence": 1,
        "evidenceReferences": [],
    }
    if media_facts is not None:
        window = job["segmentWindow"]
        del manifest["scope"]["match"]
        manifest["scope"]["segment"] = {
            "segmentId": window["segmentId"],
            "windowStartMs": window["startMs"],
            "windowEndMs": window["endMs"],
        }
        manifest["validityWindow"] = {"startMs": window["startMs"], "endMs": window["endMs"]}
        manifest["factCount"] = 1
        manifest["outputType"] = "foundational-facts"
    with open(f"{RESULT}/manifest.json", "w") as f:
        json.dump(manifest, f)


main()
