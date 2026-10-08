using Npgsql;

namespace SocAlytics.Platform.Integration.Tests.Structure;

internal enum StructureViolationKind
{
    InvalidVersionColumn,
    MissingAdvanceTrigger,
    UnclassifiedTable,
    MissingTouchTriggers,
    ChildHasVersionColumn,
    UnknownManifestEntry,
    ForbiddenName,
}

internal sealed record StructureViolation(StructureViolationKind Kind, string Subject, string Detail)
{
    public override string ToString() => $"{Kind}: {Subject} ({Detail})";
}

internal static class PersistenceStructureChecker
{
    private const string AppSchema = "socalytics";
    private const string MigrationSchema = "socalytics_migrations";
    private static readonly string[] ForbiddenNames = ["club_id", "clubid", "tenant_id"];

    private sealed record Column(string Table, string Name, string Type, bool NotNull, string? Default);

    private sealed record Trigger(string Table, string Function, int Type, string[] Args);

    public static async Task<IReadOnlyList<StructureViolation>> CheckAsync(
        string connectionString,
        IReadOnlyDictionary<string, TableClassification> manifest,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = await ReadTablesAsync(connection, cancellationToken);
        var columns = await ReadColumnsAsync(connection, cancellationToken);
        var triggers = await ReadTriggersAsync(connection, cancellationToken);
        var violations = new List<StructureViolation>();

        foreach (var (schema, name) in tables.Where(t => IsForbidden(t.Name)))
        {
            violations.Add(new(StructureViolationKind.ForbiddenName, $"{schema}.{name}", "table name"));
        }

        foreach (var column in columns.Where(c => IsForbidden(c.Name)))
        {
            violations.Add(new(StructureViolationKind.ForbiddenName, column.Table, $"column {column.Name}"));
        }

        var appTables = tables.Where(t => t.Schema == AppSchema).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var table in appTables.Order(StringComparer.Ordinal))
        {
            var version = columns.FirstOrDefault(c => c.Table == $"{AppSchema}.{table}" && c.Name == "version");
            var tableTriggers = triggers.Where(t => t.Table == table).ToList();
            manifest.TryGetValue(table, out var classification);

            if (version is not null)
            {
                CheckVersioned(table, version, tableTriggers, violations);
            }
            else if (classification is null)
            {
                violations.Add(new(StructureViolationKind.UnclassifiedTable, table, "no version column and not in the manifest"));
            }

            if (classification is ChildOfClassification child)
            {
                if (version is not null)
                {
                    violations.Add(new(StructureViolationKind.ChildHasVersionColumn, table, "child rows carry no version"));
                }

                CheckChildTriggers(table, child, tableTriggers, violations);
            }
        }

        foreach (var entry in manifest.Keys.Where(k => !appTables.Contains(k)).Order(StringComparer.Ordinal))
        {
            violations.Add(new(StructureViolationKind.UnknownManifestEntry, entry, "manifest entry has no table"));
        }

        return violations;
    }

    public static async Task<int> CountTablesAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return (await ReadTablesAsync(connection, cancellationToken)).Count(t => t.Schema == AppSchema);
    }

    private static bool IsForbidden(string name) => ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    // pg_trigger.tgtype bits: ROW = 1, BEFORE = 2, INSERT = 4, DELETE = 8, UPDATE = 16
    private const int TypeMask = 1 | 2 | 4 | 8 | 16;

    private static void CheckVersioned(string table, Column version, List<Trigger> triggers, List<StructureViolation> violations)
    {
        if (version.Type != "bigint" || !version.NotNull || version.Default != "1")
        {
            violations.Add(new(
                StructureViolationKind.InvalidVersionColumn,
                table,
                $"type {version.Type}, not null {version.NotNull}, default {version.Default ?? "none"}"));
        }

        var advance = triggers.Any(t => t.Function == "advance_version" && (t.Type & TypeMask) == (1 | 2 | 16));
        if (!advance)
        {
            violations.Add(new(StructureViolationKind.MissingAdvanceTrigger, table, "no BEFORE UPDATE row trigger executing advance_version()"));
        }
    }

    private static void CheckChildTriggers(string table, ChildOfClassification child, List<Trigger> triggers, List<StructureViolation> violations)
    {
        string[] expected = [child.Root, child.RootKey, child.ChildKey];
        var touches = triggers.Where(t => t.Function == "touch_aggregate_root" && t.Args.SequenceEqual(expected)).ToList();

        var insertDelete = touches.Any(t => (t.Type & TypeMask) == (1 | 4 | 8));
        var update = touches.Any(t => (t.Type & TypeMask) == (1 | 16));
        if (!insertDelete || !update)
        {
            violations.Add(new(
                StructureViolationKind.MissingTouchTriggers,
                table,
                $"expected both touch_aggregate_root triggers with arguments {string.Join(", ", expected)}"));
        }
    }

    private static async Task<List<(string Schema, string Name)>> ReadTablesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('socalytics', 'socalytics_migrations') AND c.relkind IN ('r', 'p')
            """;
        var result = new List<(string, string)>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<List<Column>> ReadColumnsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname || '.' || c.relname, a.attname, format_type(a.atttypid, a.atttypmod),
                   a.attnotnull, pg_get_expr(d.adbin, d.adrelid)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname IN ('socalytics', 'socalytics_migrations')
              AND c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped
            """;
        var result = new List<Column>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    private static async Task<List<Trigger>> ReadTriggersAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname, p.proname, t.tgtype::int, encode(t.tgargs, 'escape')
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_proc p ON p.oid = t.tgfoid
            JOIN pg_namespace pn ON pn.oid = p.pronamespace
            WHERE n.nspname = 'socalytics' AND pn.nspname = 'socalytics' AND NOT t.tgisinternal
            """;
        var result = new List<Trigger>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var args = reader.GetString(3).Split("\\000", StringSplitOptions.RemoveEmptyEntries);
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), args));
        }

        return result;
    }
}
