# Contract: Queue Topology and Manager Permissions

## Topology (platform-provisioned)

- **Stream** `SOCALYTICS_ANALYSIS_JOBS`: the existing stream with work-queue
  retention and subjects `analysis.jobs.ready.>`. The outbox publisher keeps
  publishing ready work to `analysis.jobs.ready.<capabilityId>` with
  `Nats-Msg-Id`.
- **Consumers**: one durable pull consumer per capability declared by the
  configured workflow definitions, with:
  - name `analyst-<capabilityId>`;
  - `FilterSubject` `analysis.jobs.ready.<capabilityId>`;
  - `AckPolicy` explicit;
  - `AckWait` `AnalystWork:ConsumerAckWait`;
  - `MaxDeliver` unlimited.

  The consumers are provisioned idempotently at platform startup with the
  existing topology provisioning.
- **Accounts** (JWT mode):
  - the operator;
  - the system account;
  - the stamp account `SOCALYTICS` with JetStream enabled.

  The platform connects with its own user of the stamp account.

## Manager user permissions

These are embedded in every Manager user JWT. Here `<inbox>` is the Manager's
`inboxPrefix`.

| Direction | Subjects |
| --- | --- |
| Publish allow | `$JS.API.CONSUMER.MSG.NEXT.SOCALYTICS_ANALYSIS_JOBS.analyst-*` |
| Publish allow | `$JS.ACK.SOCALYTICS_ANALYSIS_JOBS.>` |
| Publish allow | `$JS.API.CONSUMER.INFO.SOCALYTICS_ANALYSIS_JOBS.analyst-*` |
| Subscribe allow | `<inbox>.>` |
| Everything else | Denied (no stream or consumer administration, no publishing of work, no events) |

Possession of a delivery confers no authority: the claim operation decides
(FR-036 of the durable analysis workflow).

## Manager behavior on deliveries

| Situation | Action |
| --- | --- |
| Admitted, slot free, resources fit | Claim; acknowledge after a successful or `claim-obsolete` outcome |
| Claim fails transiently | No acknowledgement; retry the claim with the same retry key, then leave it to redelivery |
| Resources do not fit | Negatively acknowledge with the configured delay; never claim |
| Not admitted (paused, draining, runtime unavailable, revoked) | Do not fetch; a fetched but unclaimed delivery is negatively acknowledged without delay |

## Revocation

Revoking a registration (by a Club Admin, or on completion of a local
unregistration):

1. Adds every unexpired user public key issued to that Manager to the stamp
   account JWT's `revocations` map.
2. Re-signs the account JWT with the operator key.
3. Pushes it through `$SYS.REQ.CLAIMS.UPDATE`.

NATS then disconnects connections with those keys and refuses them on
reconnect (FR-006, FR-023, SC-009). Pending pushes are retried by
`QueueRevocationWorker` until acknowledged.
