# Contract: Revocation Fencing

This extends `POST /api/v1/analyst-manager-registrations/{registrationId}/revocation`
(Club Admin) and `POST /api/v1/analyst-managers/self/unregistration/completion`
(the Manager after a local unregistration). Requests and responses are
unchanged.

## Added behavior, in the existing unit of work

1. For every attempt in state `Active` whose claiming Manager is the revoked
   registration:
   - close it as `Stale`;
   - invalidate its fencing token;
   - consume one unit of its Logical Job's attempt budget;
   - make the Logical Job ready again (recording the outgoing ready-work
     message through the outbox) while budget remains, or fail it permanently,
     with descendant-blocking and run-outcome rules applied.

   This is the same logic as lease expiry, extracted into one shared step.
2. Set `revoked_at` on every unexpired issued queue credential of the
   registration.
3. Record the existing audit event, extended with the number of fenced attempts
   and revoked queue credentials (counts only, no identifiers of secrets).

After commit, push the rebuilt stamp account JWT immediately. If that fails,
`QueueRevocationWorker` retries.

## Observable outcomes

- Later claim, heartbeat, completion, and queue-credential requests of the
  Manager are refused: `401`/`403` for machine authentication, and
  `attempt-obsolete` for fenced attempts where authentication still passes in
  tests.
- The Manager's NATS connections are closed, and reconnecting with an issued
  credential is refused.
- An attempt that was the Logical Job's last permitted attempt leaves the
  Logical Job permanently failed.
- Telemetry counts fenced attempts with reason `revoked`, next to the existing
  lease-expiry reason.
