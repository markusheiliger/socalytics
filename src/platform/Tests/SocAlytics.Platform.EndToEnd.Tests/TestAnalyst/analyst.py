import hashlib
import json
import re
import sys
import time
import uuid

WORKFLOW_ID = "e2e-docker-workflow"
WORKFLOW_VERSION = "1"
# Keeps the container running long enough for the test to inspect it.
HOLD_SECONDS = 4
RESULT = "/socalytics/result"


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


def main():
    mode = read_mode()
    with open("/socalytics/job.json") as f:
        job = json.load(f)

    time.sleep(3600 if mode == "hold" else HOLD_SECONDS)
    if mode == "fail":
        sys.exit(3)
    if mode == "no-manifest":
        return

    artifact = json.dumps({"logicalJobId": job["logicalJobId"], "ok": True}, sort_keys=True).encode()
    with open(f"{RESULT}/summary.json", "wb") as f:
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
                "name": "summary.json",
                "digest": "sha-256:" + hashlib.sha256(artifact).hexdigest(),
                "sizeBytes": len(artifact),
                "mediaType": "application/json",
            }
        ],
        "validityWindow": {"startMs": 0, "endMs": 1000},
        "factCount": 0,
        "upstreamLineage": [],
        "outputType": "soccer-concepts",
        "confidence": 1,
        "evidenceReferences": [],
    }
    with open(f"{RESULT}/manifest.json", "w") as f:
        json.dump(manifest, f)


main()
