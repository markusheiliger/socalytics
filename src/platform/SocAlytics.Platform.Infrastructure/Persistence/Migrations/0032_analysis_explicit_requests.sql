CREATE TABLE socalytics.analysis_run_requests (
    idempotency_key text PRIMARY KEY,
    match_id uuid NOT NULL REFERENCES socalytics.match (id),
    requested_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    request_id uuid NOT NULL UNIQUE REFERENCES socalytics.analysis_notification_receipts (message_id),
    outcome text NOT NULL,
    rejection_category text NULL,
    recording_set_version_id uuid NULL,
    run_id uuid NULL REFERENCES socalytics.analysis_runs (id),
    current_run_id uuid NULL REFERENCES socalytics.analysis_runs (id),
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_analysis_run_requests_key CHECK (idempotency_key ~ '^[!-~]{1,255}$'),
    CONSTRAINT ck_analysis_run_requests_outcome CHECK (outcome IN ('run-created', 'run-exists', 'rejected')),
    CONSTRAINT ck_analysis_run_requests_category CHECK (
        (outcome = 'rejected' AND rejection_category IN ('no-finalized-recording-set', 'not-configured', 'invalid-definition'))
        OR (outcome <> 'rejected' AND rejection_category IS NULL)),
    CONSTRAINT ck_analysis_run_requests_run CHECK ((outcome = 'rejected') = (run_id IS NULL)),
    CONSTRAINT ck_analysis_run_requests_set CHECK (
        recording_set_version_id IS NOT NULL OR rejection_category = 'no-finalized-recording-set')
);

CREATE TRIGGER trg_analysis_run_requests_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_run_requests
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON socalytics.analysis_run_requests FROM socalytics_app;

ALTER TABLE socalytics.analysis_notification_receipts DROP CONSTRAINT ck_analysis_notification_receipts_type;
ALTER TABLE socalytics.analysis_notification_receipts
    ADD CONSTRAINT ck_analysis_notification_receipts_type CHECK (message_type IN ('recordings-finalized', 'analysis-requested'));

ALTER TABLE socalytics.analysis_notification_receipts DROP CONSTRAINT ck_analysis_notification_receipts_category;
ALTER TABLE socalytics.analysis_notification_receipts
    ADD CONSTRAINT ck_analysis_notification_receipts_category CHECK (
        rejection_category IS NULL
        OR rejection_category IN ('schema-invalid', 'lineage-not-found', 'lineage-mismatch', 'scope-mismatch',
                                  'no-finalized-recording-set', 'not-configured', 'invalid-definition'));
