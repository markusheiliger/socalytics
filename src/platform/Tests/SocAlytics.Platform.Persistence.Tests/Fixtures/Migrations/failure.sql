CREATE TABLE club.failed_probe (value text);
INSERT INTO club.migration_probe (value) VALUES ('failed');
DO $$
BEGIN
    RAISE EXCEPTION 'provider-detail-must-not-leak';
END
$$;
