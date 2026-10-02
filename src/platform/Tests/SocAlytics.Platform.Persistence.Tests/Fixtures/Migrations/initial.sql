CREATE SCHEMA club;
CREATE TABLE club.migration_probe (value text NOT NULL);
INSERT INTO club.migration_probe (value) VALUES ('initial');
