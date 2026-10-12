CREATE TABLE socalytics.outbox_messages (
    id uuid PRIMARY KEY,
    message_type text NOT NULL,
    contract_version text NOT NULL,
    subject text NOT NULL,
    payload jsonb NOT NULL,
    causation_id uuid NOT NULL,
    run_id uuid NULL,
    logical_job_id uuid NULL,
    capability_id text NULL,
    state text NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    publication_round integer NOT NULL DEFAULT 1,
    last_attempt_at timestamptz NULL,
    next_attempt_at timestamptz NULL,
    failure_category text NULL,
    created_at timestamptz NOT NULL,
    published_at timestamptz NULL,
    CONSTRAINT ck_outbox_messages_message_type CHECK (message_type IN (
        'matches.recordings-finalized', 'analysis.ready-work', 'analysis.run-state-changed')),
    CONSTRAINT ck_outbox_messages_state CHECK (state IN ('pending', 'published', 'failed', 'discarded')),
    CONSTRAINT ck_outbox_messages_attempt_count CHECK (attempt_count >= 0),
    CONSTRAINT ck_outbox_messages_publication_round CHECK (publication_round >= 1),
    CONSTRAINT ck_outbox_messages_failure_category CHECK (failure_category IS NULL OR failure_category IN (
        'transport-unavailable', 'publish-timeout', 'publish-rejected')),
    CONSTRAINT uq_outbox_messages_type_causation UNIQUE (message_type, causation_id)
);

CREATE INDEX ix_outbox_messages_state_next_attempt ON socalytics.outbox_messages (state, next_attempt_at);
CREATE INDEX ix_outbox_messages_state_created ON socalytics.outbox_messages (state, created_at);

INSERT INTO socalytics.outbox_messages
    (id, message_type, contract_version, subject, payload, causation_id, state, attempt_count, publication_round, created_at)
SELECT event_id, 'matches.recordings-finalized', contract_version, 'matches.recordings-finalized', payload, event_id, 'pending', 0, 1, occurred_at
FROM socalytics.recording_finalized_events
ON CONFLICT DO NOTHING;
