using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class BreakGlassRecoveryEvidenceTests(PostgresContainerFixture postgres)
{
	private const string Admin = "evidence-admin";
	private const string InitialPassword = "Evidence-Initial-Pass-1234";
	private const string Temporary = "Evidence-Temporary-Pass-5678";
	private const string PolicyViolating = "zq7short";
	private const string Changed = "Evidence-Changed-Pass-91011";

	private static Dictionary<string, string?> Bootstrap() => new()
	{
		["ClubDisplayName"] = "Evidence Club",
		["FirstClubAdmin:AccountName"] = Admin,
		["FirstClubAdmin:InitialPassword"] = InitialPassword,
	};

	private static Dictionary<string, string?> Directive(string id, string credential) => new()
	{
		["AccountName"] = Admin,
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

	private static string LogText(PlatformApiFactory factory) =>
		string.Join('\n', factory.Logs.Entries.Select(e =>
			e.Message + " " + string.Join(' ', e.State.Select(s => s.Key + "=" + s.Value)) + " " + e.Exception));

	[Fact]
	// Quickstart A42
	public async Task RestrictedSessionIsLimitedUntilPasswordChanged()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db, Bootstrap(), Directive("evidence-0001", Temporary));
		await factory.WaitUntilHealthyAsync(ct);

		using var session = await ApiSession.SignInAsync(factory, Admin, Temporary, ct);
		session.Info.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();

		foreach (var path in new[] { "/api/v1/club", "/api/v1/seasons", "/api/v1/teams", "/api/v1/members" })
		{
			using var response = await session.SendAsync(HttpMethod.Get, path, null, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
			(await response.Content.ReadAsStringAsync(ct)).ShouldContain("password-change-required");
		}

		using var change = await session.SendAsync(
			HttpMethod.Post, "/api/v1/me/password", new { currentPassword = Temporary, newPassword = Changed }, ct);
		change.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var after = await session.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
		after.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	// Quickstart A44
	public async Task ConcurrentHostsApplyTheSameDirectiveExactlyOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using (var seed = new PlatformApiFactory(db, Bootstrap()))
		{
			await seed.WaitUntilHealthyAsync(ct);
		}

		var factories = Enumerable.Range(0, 4)
			.Select(_ => new PlatformApiFactory(db, Bootstrap(), Directive("evidence-0002", Temporary)))
			.ToList();
		try
		{
			await Task.WhenAll(factories.Select(f => f.WaitUntilHealthyAsync(ct, TimeSpan.FromSeconds(120))));

			(await CountAsync(db, "SELECT count(*) FROM socalytics.recovery_directive_use", ct)).ShouldBe(1);
			(await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'break-glass-recovery.applied'", ct)).ShouldBe(1);
			(await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'break-glass-recovery.refused' AND reason_code = 'recovery-id-used'", ct)).ShouldBe(3);
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
	// Quickstart A48
	public async Task TemporaryCredentialsAppearNowhereAndNoEndpointAcceptsDirectives()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var bodies = new List<string>();
		var logs = new List<string>();

		await using (var seed = new PlatformApiFactory(db, Bootstrap()))
		{
			await seed.WaitUntilHealthyAsync(ct);
			logs.Add(LogText(seed));
		}

		await using (var refused = new PlatformApiFactory(db, Bootstrap(), Directive("evidence-0003", PolicyViolating)))
		{
			await refused.WaitUntilHealthyAsync(ct);
			logs.Add(LogText(refused));
		}

		await using (var applied = new PlatformApiFactory(db, Bootstrap(), Directive("evidence-0004", Temporary)))
		{
			await applied.WaitUntilHealthyAsync(ct);

			using (var anonymous = applied.CreateApiClient())
			{
				using var rejected = await anonymous.PostAsJsonAsync(
					"/api/v1/session", new { accountName = Admin, password = PolicyViolating }, ct);
				bodies.Add(await rejected.Content.ReadAsStringAsync(ct));
			}

			using var session = await ApiSession.SignInAsync(applied, Admin, Temporary, ct);
			bodies.Add(session.Info.GetRawText());
			using var restricted = await session.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
			bodies.Add(await restricted.Content.ReadAsStringAsync(ct));

			var routes = applied.Services.GetRequiredService<EndpointDataSource>().Endpoints
				.OfType<RouteEndpoint>()
				.Select(e => e.RoutePattern.RawText ?? string.Empty);
			routes.ShouldNotContain(r => r.Contains("recover", StringComparison.OrdinalIgnoreCase)
				|| r.Contains("break-glass", StringComparison.OrdinalIgnoreCase)
				|| r.Contains("directive", StringComparison.OrdinalIgnoreCase));

			logs.Add(LogText(applied));
		}

		foreach (var secret in new[] { Temporary, PolicyViolating })
		{
			(await CountAsync(db, $"SELECT count(*) FROM socalytics.security_audit_event e WHERE e::text LIKE '%{secret}%'", ct)).ShouldBe(0);
			logs.ShouldAllBe(l => !l.Contains(secret, StringComparison.Ordinal));
			bodies.ShouldAllBe(b => !b.Contains(secret, StringComparison.Ordinal));
		}
	}
}
