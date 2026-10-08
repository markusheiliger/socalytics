using Microsoft.AspNetCore.Identity;
using Npgsql;
using SocAlytics.Platform.Infrastructure.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.Infrastructure;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal static class TestMembers
{
	public const string DefaultPassword = "correct-horse-battery";

	public static async Task<Guid> SeedAsync(
		IsolatedDatabase db,
		string accountName,
		string? password = DefaultPassword,
		string membershipStatus = "active",
		bool passwordChangeRequired = false,
		bool lockedOut = false,
		IEnumerable<string>? clubRoles = null,
		CancellationToken cancellationToken = default)
	{
		var id = Guid.NewGuid();
		var now = DateTimeOffset.UtcNow;
		var hash = password is null
			? null
			: new PasswordHasher<IdentityMemberAccount>().HashPassword(new IdentityMemberAccount(), password);

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(cancellationToken);
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

		await using (var command = new NpgsqlCommand(
			"INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, password_hash, security_stamp, " +
			"membership_status, membership_changed_at, created_at, password_change_required, lockout_end) " +
			"VALUES (@id, @n, @nn, @h, @stamp, @s, @now, @now, @pcr, @lock)",
			connection,
			transaction))
		{
			command.Parameters.AddWithValue("id", id);
			command.Parameters.AddWithValue("n", accountName);
			command.Parameters.AddWithValue("nn", accountName.ToUpperInvariant());
			command.Parameters.AddWithValue("h", (object?)hash ?? DBNull.Value);
			command.Parameters.AddWithValue("stamp", Guid.NewGuid().ToString("N"));
			command.Parameters.AddWithValue("s", membershipStatus);
			command.Parameters.AddWithValue("now", now);
			command.Parameters.AddWithValue("pcr", passwordChangeRequired);
			command.Parameters.AddWithValue("lock", lockedOut ? now.AddYears(1) : DBNull.Value);
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		foreach (var role in clubRoles ?? [])
		{
			await using var command = new NpgsqlCommand(
				"INSERT INTO socalytics.club_role_assignment (member_account_id, role, assigned_at) VALUES (@id, @r, @now)",
				connection,
				transaction);
			command.Parameters.AddWithValue("id", id);
			command.Parameters.AddWithValue("r", role);
			command.Parameters.AddWithValue("now", now);
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);
		return id;
	}
}
