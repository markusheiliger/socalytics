ALTER TABLE socalytics.analysis_runs
    ADD COLUMN cancelled_by_account_id uuid NULL REFERENCES socalytics.member_account (id),
    ADD COLUMN cancellation_reason text NULL;

ALTER TABLE socalytics.analysis_runs
    ADD CONSTRAINT ck_analysis_runs_cancelled_by CHECK ((cancelled_by_account_id IS NOT NULL) = (state = 'Cancelled')),
    ADD CONSTRAINT ck_analysis_runs_cancellation_reason CHECK (cancellation_reason IS NULL OR state = 'Cancelled'),
    ADD CONSTRAINT ck_analysis_runs_cancellation_reason_length CHECK (cancellation_reason IS NULL OR char_length(cancellation_reason) <= 500);

ALTER TABLE socalytics.analysis_workflow_nodes DROP CONSTRAINT ck_analysis_workflow_nodes_state;
ALTER TABLE socalytics.analysis_workflow_nodes
    ADD CONSTRAINT ck_analysis_workflow_nodes_state CHECK (state IN ('Waiting', 'Active', 'Completed', 'Failed', 'Blocked', 'Cancelled'));

ALTER TABLE socalytics.analysis_logical_jobs DROP CONSTRAINT ck_analysis_logical_jobs_state;
ALTER TABLE socalytics.analysis_logical_jobs
    ADD CONSTRAINT ck_analysis_logical_jobs_state CHECK (state IN ('Waiting', 'Ready', 'Claimed', 'Completed', 'Failed', 'Blocked', 'Cancelled'));

ALTER TABLE socalytics.analysis_execution_attempts DROP CONSTRAINT ck_analysis_execution_attempts_state;
ALTER TABLE socalytics.analysis_execution_attempts
    ADD CONSTRAINT ck_analysis_execution_attempts_state CHECK (state IN ('Active', 'Stale', 'Failed', 'Accepted', 'Rejected', 'Cancelled'));
