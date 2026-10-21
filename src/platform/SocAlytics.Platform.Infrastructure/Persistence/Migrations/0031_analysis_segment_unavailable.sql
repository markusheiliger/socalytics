ALTER TABLE socalytics.analysis_logical_jobs DROP CONSTRAINT ck_analysis_logical_jobs_failure_reason;
ALTER TABLE socalytics.analysis_logical_jobs
    ADD CONSTRAINT ck_analysis_logical_jobs_failure_reason CHECK (
        failure_reason IS NULL
        OR failure_reason IN ('attempts-exhausted', 'upstream-required-failed', 'upstream-coverage-below-minimum', 'segment-unavailable'));
