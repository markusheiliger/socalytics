ALTER TABLE socalytics.analysis_runs
    ADD COLUMN recording_set_finalized_at timestamptz NULL,
    ADD COLUMN cancellation_cause text NULL;

UPDATE socalytics.analysis_runs r
SET recording_set_finalized_at = v.finalized_at
FROM socalytics.recording_set_versions v
WHERE v.id = r.recording_set_version_id;

-- Runs whose recording-set version no longer exists keep their creation time as the version time.
UPDATE socalytics.analysis_runs
SET recording_set_finalized_at = created_at
WHERE recording_set_finalized_at IS NULL;

UPDATE socalytics.analysis_runs
SET cancellation_cause = 'member'
WHERE state = 'Cancelled';

ALTER TABLE socalytics.analysis_runs
    ALTER COLUMN recording_set_finalized_at SET NOT NULL;

ALTER TABLE socalytics.analysis_runs
    DROP CONSTRAINT ck_analysis_runs_cancelled_by,
    DROP CONSTRAINT ck_analysis_runs_cancellation_reason;

ALTER TABLE socalytics.analysis_runs
    ADD CONSTRAINT ck_analysis_runs_cancellation_cause CHECK (
        cancellation_cause IN ('member', 'superseded') AND (cancellation_cause IS NOT NULL) = (state = 'Cancelled')),
    ADD CONSTRAINT ck_analysis_runs_cancelled_by CHECK ((cancelled_by_account_id IS NOT NULL) = coalesce(cancellation_cause = 'member', false)),
    ADD CONSTRAINT ck_analysis_runs_cancellation_reason CHECK (cancellation_reason IS NULL OR cancellation_cause = 'member'),
    ADD CONSTRAINT uq_analysis_runs_id_match UNIQUE (id, match_id);

CREATE INDEX ix_analysis_runs_match_order
    ON socalytics.analysis_runs (match_id, recording_set_finalized_at DESC, recording_set_version_id DESC, created_at DESC, id DESC);

CREATE TABLE socalytics.analysis_run_supersessions (
    run_id uuid PRIMARY KEY,
    match_id uuid NOT NULL,
    superseding_run_id uuid NOT NULL,
    superseded_at timestamptz NOT NULL,
    previous_state text NOT NULL,
    CONSTRAINT ck_analysis_run_supersessions_previous_state CHECK (previous_state IN ('Requested', 'Running', 'Completed', 'PartiallyCompleted', 'Failed', 'Cancelled')),
    CONSTRAINT ck_analysis_run_supersessions_distinct CHECK (run_id <> superseding_run_id),
    CONSTRAINT fk_analysis_run_supersessions_run FOREIGN KEY (run_id, match_id) REFERENCES socalytics.analysis_runs (id, match_id),
    CONSTRAINT fk_analysis_run_supersessions_superseding FOREIGN KEY (superseding_run_id, match_id) REFERENCES socalytics.analysis_runs (id, match_id)
);

CREATE INDEX ix_analysis_run_supersessions_superseding ON socalytics.analysis_run_supersessions (superseding_run_id);
