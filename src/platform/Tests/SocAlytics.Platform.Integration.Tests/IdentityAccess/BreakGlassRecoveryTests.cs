using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class BreakGlassRecoveryTests(PostgresContainerFixture postgres)
{
	private const string Admin = "recovery-admin";
	private const string InitialPassword = "Recovery-Initial-Pass-1234";
	private const string Temporary = "Recovery-Temporary-Pass-5678";
	private const string RecoveryId = "recovery-0001";

	private static Dictionary<string, string?> Bootstrap(string account = Admin) => new()
	{
		["ClubDisplayName"] = "Recovery Club",
		["FirstClubAdmin:AccountName"] = account,
		["FirstClubAdmin:InitialPassword"] = InitialPassword,
	};

	private static Dictionary<string, string?> Directive(string account = Admin, string? id = RecoveryId, string? credential = Temporary) => new()
	{
		["AccountName"] = account,
		["RecoveryId"] = id,
		["TemporaryCredential"] = credential,
	};

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	private static Task<long> EventsAsync(IsolatedDatabase db, string type, CancellationToken ct, string? reason = null) =>
		CountAsync(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{type}'" + (reason is null ? "" : $" AND reason_code = '{reason}'"), ct);

	private static async Task<HttpStatusCode> SignInAsync(PlatformApiFactory factory, string name, string password, CancellationToken ct)
	{
		using var client = factory.CreateApiClient();
		using var response = await client.PostAsJsonAsync("/api/v1/session", new { accountName = name, password }, ct);
		return response.StatusCode;
	}

	[Fact]
	// Quickstart A41
	public async Task ValidDirectiveRecoversLockedOutAdminOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		ApiSession oldSession;
		await using (var first = new PlatformApiFactory(db, Bootstrap()))
		{
			await first.WaitUntilHealthyAsync(ct);
			oldSession = await ApiSession.SignInAsync(first, Admin, InitialPassword, ct);
			for (var i = 0; i < 5; i++)
			{
				(await SignInAsync(first, Admin, "wrong-password-value", ct)).ShouldBe(HttpStatusCode.Unauthorized);
			}
		}

		using (oldSession)
		{
			await using var second = new PlatformApiFactory(db, Bootstrap(), Directive());
			await second.WaitUntilHealthyAsync(ct);

			(await CountAsync(db, "SELECT count(*) FROM socalytics.recovery_directive_use", ct)).ShouldBe(1);
			(await EventsAsync(db, "break-glass-recovery.applied", ct)).ShouldBe(1);
			(await CountAsync(db, "SELECT count(*) FROM socalytics.club_role_assignment WHERE role = 'club-admin'", ct)).ShouldBe(1);
			(await CountAsync(db, "SELECT count(*) FROM socalytics.member_account WHERE lockout_end IS NOT NULL OR access_failed_count <> 0", ct)).ShouldBe(0);
			(await CountAsync(db, "SELECT count(*) FROM socalytics.member_account WHERE password_change_required", ct)).ShouldBe(1);
			(await SignInAsync(second, Admin, Temporary, ct)).ShouldBe(HttpStatusCode.OK);
			(await second.CheckBootstrapAsync(ct)).Status.ShouldBe(HealthStatus.Healthy);
		}
	}

	[Fact]
	// Quickstart A43
	public async Task RestartWithUsedDirectiveChangesNothingAndRecordsRefusal()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using (var first = new PlatformApiFactory(db, Bootstrap(), Directive()))
		{
			await first.WaitUntilHealthyAsync(ct);
			using var session = await ApiSession.SignInAsync(first, Admin, Temporary, ct);
			using var change = await session.SendAsync(
				HttpMethod.Post, "/api/v1/me/password", new { currentPassword = Temporary, newPassword = "Changed-Password-91011" }, ct);
			change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		await using var second = new PlatformApiFactory(db, Bootstrap(), Directive());
		await second.WaitUntilHealthyAsync(ct);
		(await SignInAsync(second, Admin, "Changed-Password-91011", ct)).ShouldBe(HttpStatusCode.OK);
		(await CountAsync(db, "SELECT count(*) FROM socalytics.recovery_directive_use", ct)).ShouldBe(1);
		(await EventsAsync(db, "break-glass-recovery.applied", ct)).ShouldBe(1);
		(await EventsAsync(db, "break-glass-recovery.refused", ct, "recovery-id-used")).ShouldBe(1);
	}

	[Theory]
	[InlineData("unknown-account")]
	[InlineData("account-inactive")]
	[InlineData("password-policy")]
	[InlineData("directive-incomplete")]
	// Quickstart A45
	public async Task RefusedDirectivesChangeNothingAndKeepIdUnused(string reason)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using (var first = new PlatformApiFactory(db, Bootstrap()))
		{
			await first.WaitUntilHealthyAsync(ct);
		}

		if (reason == "account-inactive")
		{
			await TestMembers.SeedAsync(db, "inactive-1", membershipStatus: "deactivated", cancellationToken: ct);
		}

		var directive = reason switch
		{
			"unknown-account" => Directive("nobody-here"),
			"account-inactive" => Directive("inactive-1"),
			"password-policy" => Directive(credential: "short"),
			_ => Directive(id: null),
		};
		await using var second = new PlatformApiFactory(db, Bootstrap(), directive);
		await second.WaitUntilHealthyAsync(ct);

		(await CountAsync(db, "SELECT count(*) FROM socalytics.recovery_directive_use", ct)).ShouldBe(0);
		(await EventsAsync(db, "break-glass-recovery.refused", ct, reason)).ShouldBe(1);
		(await EventsAsync(db, "break-glass-recovery.applied", ct)).ShouldBe(0);
		(await SignInAsync(second, Admin, InitialPassword, ct)).ShouldBe(HttpStatusCode.OK);
		(await second.CheckBootstrapAsync(ct)).Status.ShouldBe(HealthStatus.Healthy);
	}

	[Fact]
	// Quickstart A46
	public async Task RecoveringAccountWithoutClubAdminGrantsNoRole()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "plain-member", cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db, Bootstrap(), Directive("plain-member"));
		await factory.WaitUntilHealthyAsync(ct);

		(await EventsAsync(db, "break-glass-recovery.applied", ct)).ShouldBe(1);
		(await CountAsync(db, "SELECT count(*) FROM socalytics.club_role_assignment cr JOIN socalytics.member_account a ON a.id = cr.member_account_id WHERE a.account_name = 'plain-member'", ct)).ShouldBe(0);
		(await CountAsync(db, "SELECT count(*) FROM socalytics.member_account WHERE account_name = 'plain-member' AND password_change_required", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task FreshDatabaseCommitsBootstrapAndUnknownAccountRefusalTogether()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db, Bootstrap(), Directive("nobody-here"));
		await factory.WaitUntilHealthyAsync(ct);

		(await CountAsync(db, "SELECT count(*) FROM socalytics.club", ct)).ShouldBe(1);
		(await CountAsync(db, "SELECT count(*) FROM socalytics.club_role_assignment", ct)).ShouldBe(1);
		(await EventsAsync(db, "club.bootstrapped", ct)).ShouldBe(1);
		(await EventsAsync(db, "break-glass-recovery.refused", ct, "unknown-account")).ShouldBe(1);
	}

	[Fact]
	public async Task ConflictingBootstrapStaysUnhealthyButDirectiveStillApplies()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using (var first = new PlatformApiFactory(db, Bootstrap()))
		{
			await first.WaitUntilHealthyAsync(ct);
		}

		await using var second = new PlatformApiFactory(db, Bootstrap("other-admin"), Directive());
		using var client = second.CreateApiClient();
		var deadline = DateTime.UtcNow.AddSeconds(60);
		while (await EventsAsync(db, "break-glass-recovery.applied", ct) == 0)
		{
			(DateTime.UtcNow < deadline).ShouldBeTrue();
			await Task.Delay(200, ct);
		}

		var entry = await second.CheckBootstrapAsync(ct);
		entry.Status.ShouldBe(HealthStatus.Unhealthy);
		entry.Description.ShouldBe("bootstrap-conflict");
		(await EventsAsync(db, "club.bootstrap-refused", ct)).ShouldBe(1);
		(await CountAsync(db, "SELECT count(*) FROM socalytics.recovery_directive_use", ct)).ShouldBe(1);
	}
}
