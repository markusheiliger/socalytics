# Data Model: Invitation Audit Trail Test Flake

No data model changes. The test reads existing records only:

- `socalytics.security_audit_event` (`event_type`, `outcome`, `reason_code`, `details->>'maskedAddress'`): one `invitation.issued` event per issued link, with the masked address of the invitee.
- `socalytics.email_messages` and `socalytics.member_invitation_links`: the outbox message and the link it issues; a message is claimed under `FOR UPDATE SKIP LOCKED`, and its link is issued and audited in the claiming transaction.

The only data change is the test's own input: the first invitee's address is chosen so that its masked form is unique among the test's invitees.
