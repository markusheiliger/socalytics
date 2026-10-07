CREATE FUNCTION socalytics.advance_version()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
AS $advance_version$
BEGIN
    IF current_setting('socalytics.suppress_version', true) = 'on'
        AND pg_has_role(current_user, 'socalytics_migrator', 'MEMBER') THEN
        NEW.version := OLD.version;
    ELSE
        NEW.version := OLD.version + 1;
    END IF;

    RETURN NEW;
END
$advance_version$;

CREATE FUNCTION socalytics.touch_aggregate_root()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
AS $touch_aggregate_root$
DECLARE
    touch_sql text;
BEGIN
    IF current_setting('socalytics.suppress_version', true) = 'on'
        AND pg_has_role(current_user, 'socalytics_migrator', 'MEMBER') THEN
        RETURN NULL;
    END IF;

    touch_sql := format(
        'UPDATE socalytics.%I SET version = version + 1 WHERE %I = ($1).%I',
        TG_ARGV[0], TG_ARGV[1], TG_ARGV[2]);

    IF TG_OP = 'DELETE' THEN
        EXECUTE touch_sql USING OLD;
    ELSE
        EXECUTE touch_sql USING NEW;

        IF TG_OP = 'UPDATE'
            AND to_jsonb(OLD) -> TG_ARGV[2] IS DISTINCT FROM to_jsonb(NEW) -> TG_ARGV[2] THEN
            EXECUTE touch_sql USING OLD;
        END IF;
    END IF;

    RETURN NULL;
END
$touch_aggregate_root$;

CREATE PROCEDURE socalytics.attach_version_trigger(target regclass)
LANGUAGE plpgsql
AS $attach_version_trigger$
DECLARE
    target_name name;
BEGIN
    SELECT relname INTO target_name FROM pg_class WHERE oid = target;

    EXECUTE format(
        'CREATE TRIGGER %I BEFORE UPDATE ON %s FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION socalytics.advance_version()',
        target_name || '_version_advance', target);
END
$attach_version_trigger$;

CREATE PROCEDURE socalytics.attach_aggregate_child_triggers(
    child regclass,
    root regclass,
    child_key name,
    root_key name DEFAULT 'id')
LANGUAGE plpgsql
AS $attach_aggregate_child_triggers$
DECLARE
    child_name name;
    root_name name;
BEGIN
    SELECT relname INTO child_name FROM pg_class WHERE oid = child;
    SELECT relname INTO root_name FROM pg_class WHERE oid = root;

    EXECUTE format(
        'CREATE TRIGGER %I AFTER INSERT OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION socalytics.touch_aggregate_root(%L, %L, %L)',
        child_name || '_root_touch', child, root_name, root_key, child_key);

    EXECUTE format(
        'CREATE TRIGGER %I AFTER UPDATE ON %s FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION socalytics.touch_aggregate_root(%L, %L, %L)',
        child_name || '_root_touch_update', child, root_name, root_key, child_key);
END
$attach_aggregate_child_triggers$;

REVOKE EXECUTE ON PROCEDURE socalytics.attach_version_trigger(regclass) FROM PUBLIC;
REVOKE EXECUTE ON PROCEDURE socalytics.attach_aggregate_child_triggers(regclass, regclass, name, name) FROM PUBLIC;
