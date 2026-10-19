ALTER TABLE socalytics.analysis_dependency_edges
    ADD COLUMN coverage_mode text NOT NULL DEFAULT 'complete',
    ADD COLUMN minimum_accepted_fraction numeric NULL;

ALTER TABLE socalytics.analysis_dependency_edges
    ADD CONSTRAINT ck_analysis_dependency_edges_coverage_mode CHECK (coverage_mode IN ('complete', 'partial'));
ALTER TABLE socalytics.analysis_dependency_edges
    ADD CONSTRAINT ck_analysis_dependency_edges_minimum_accepted_fraction CHECK (
        minimum_accepted_fraction IS NULL
        OR (coverage_mode = 'partial' AND minimum_accepted_fraction > 0 AND minimum_accepted_fraction <= 1));

ALTER TABLE socalytics.analysis_logical_jobs DROP CONSTRAINT ck_analysis_logical_jobs_failure_reason;
ALTER TABLE socalytics.analysis_logical_jobs
    ADD CONSTRAINT ck_analysis_logical_jobs_failure_reason CHECK (
        failure_reason IS NULL
        OR failure_reason IN ('attempts-exhausted', 'upstream-required-failed', 'upstream-coverage-below-minimum'));
