using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class ClubBootstrapHostingTests(PostgresContainerFixture postgres)
{
	private const string Password = "Hosting-Initial-Pass-1234";

	private static Dictionary<string, string?> Config(string account = "hosting-admin", string? password = Password) => new()
	{
		["ClubDisplayName"] = "Hosting Club",
		["FirstClubAdmin:AccountName"] = account,
		["FirstClubAdmin:InitialPassword"] = password,
	};

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	[Fact]
	// Quickstart A1
	public async Task ConfiguredHostBecomesHealthyWithOneClub()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db, Config());
		await factory.WaitUntilHealthyAsync(ct);

		(await factory.CheckBootstrapAsync(ct)).Status.ShouldBe(HealthStatus.Healthy);
		(await CountAsync(db, "SELECT count(*) FROM socalytics.club", ct)).ShouldBe(1);
	}

	[Fact]
	// Quickstart A3
	public async Task ConcurrentHostsProduceOneClubAndAdmin()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var factories = Enumerable.Range(0, 4).Select(_ => new PlatformApiFactory(db, Config())).ToList();
		try
		{
			await Task.WhenAll(factories.Select(f => f.WaitUntilHealthyAsync(ct)));
			(await CountAsync(db, "SELECT count(*) FROM socalytics.club", ct)).ShouldBe(1);
			(await CountAsync(db, "SELECT count(*) FROM socalytics.member_account", ct)).ShouldBe(1);
			(await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'club.bootstrapped'", ct)).ShouldBe(1);
		}
		finally
		{
			foreach (var factory in factories)
			{
				await factory.DisposeAsync();
			}
		}
	}

	[Fact]
	// Quickstart A4
	public async Task RestartWithDifferentAccountNameReportsConflict()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using (var first = new PlatformApiFactory(db, Config()))
		{
			await first.WaitUntilHealthyAsync(ct);
		}

		await using var second = new PlatformApiFactory(db, Config("other-admin"));
		using var client = second.CreateApiClient();
		await Task.Delay(TimeSpan.FromSeconds(3), ct);
		var entry = await second.CheckBootstrapAsync(ct);
		entry.Status.ShouldBe(HealthStatus.Unhealthy);
		entry.Description.ShouldBe("bootstrap-conflict");
	}

	[Fact]
	// Quickstart A5
	public async Task HostWithoutConfigurationStaysNotEstablished()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db);
		using var client = factory.CreateApiClient();
		await Task.Delay(TimeSpan.FromSeconds(2), ct);

		var entry = await factory.CheckBootstrapAsync(ct);
		entry.Status.ShouldBe(HealthStatus.Unhealthy);
		entry.Description.ShouldBe("club-not-established");
		(await client.GetAsync("/setup", ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task HostWithoutConnectionStringStartsAndLogsMissingKey()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new PlatformApiFactory(null, Config());
		using var client = factory.CreateApiClient();
		await Task.Delay(TimeSpan.FromSeconds(1), ct);

		factory.Logs.Entries.ShouldContain(e => e.Message.Contains("ConnectionStrings:socalytics"));
		(await factory.CheckBootstrapAsync(ct)).Description.ShouldBe("club-not-established");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	// Quickstart A50
	public async Task RestartWithoutPasswordOrSectionStaysHealthyWithoutNewAudit(bool removeSection)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using (var first = new PlatformApiFactory(db, Config()))
		{
			await first.WaitUntilHealthyAsync(ct);
		}

		const string auditSql = "SELECT count(*) FROM socalytics.security_audit_event";
		var before = await CountAsync(db, auditSql, ct);
		await using var second = new PlatformApiFactory(db, removeSection ? null : Config(password: null));
		await second.WaitUntilHealthyAsync(ct);

		(await CountAsync(db, auditSql, ct)).ShouldBe(before);
	}

	[Fact]
	public async Task LogsNeverContainInitialPassword()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db, Config());
		await factory.WaitUntilHealthyAsync(ct);

		factory.Logs.Entries.ShouldNotContain(e =>
			e.Message.Contains(Password) || (e.Exception ?? "").Contains(Password) || e.State.Values.Any(v => v != null && v.Contains(Password)));
	}
}
