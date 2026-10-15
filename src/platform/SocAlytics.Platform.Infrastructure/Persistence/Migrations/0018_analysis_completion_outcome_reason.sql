ALTER TABLE socalytics.analysis_completion_outcomes ADD COLUMN reason text NULL;

ALTER TABLE socalytics.analysis_completion_outcomes DROP CONSTRAINT ck_analysis_completion_outcomes_outcome;
ALTER TABLE socalytics.analysis_completion_outcomes
    ADD CONSTRAINT ck_analysis_completion_outcomes_outcome CHECK (outcome IN ('accepted', 'failure-recorded', 'failed'));
ALTER TABLE socalytics.analysis_completion_outcomes
    ADD CONSTRAINT ck_analysis_completion_outcomes_reason CHECK ((outcome = 'failed') = (reason IS NOT NULL));
