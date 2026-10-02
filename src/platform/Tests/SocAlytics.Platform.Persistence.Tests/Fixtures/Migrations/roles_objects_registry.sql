CREATE SCHEMA registry;
CREATE TABLE registry.items (id integer PRIMARY KEY, value text NOT NULL);
INSERT INTO registry.items VALUES (1, 'registry');
CREATE FUNCTION registry.secret() RETURNS integer LANGUAGE sql AS 'SELECT 1';
