using Npgsql;

namespace SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;

internal sealed record MigrationHistoryEntry(int Sequence, string Identity, string Checksum);

internal static class MigrationHistoryStore
{
    private const string UndefinedTable = "42P01";

    public static async Task<IReadOnlyList<MigrationHistoryEntry>> ReadEntriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var entries = new List<MigrationHistoryEntry>();
        try
        {
            await using var command = new NpgsqlCommand(MigrationHistorySql.SelectEntries, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new MigrationHistoryEntry(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == UndefinedTable)
        {
            return [];
        }

        return entries;
    }
}
