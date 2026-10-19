ALTER TABLE socalytics.outbox_messages DROP CONSTRAINT ck_outbox_messages_message_type;
ALTER TABLE socalytics.outbox_messages ADD CONSTRAINT ck_outbox_messages_message_type CHECK (message_type IN (
    'matches.recordings-finalized', 'analysis.ready-work', 'analysis.run-state-changed', 'agent-invocation-ready'));
