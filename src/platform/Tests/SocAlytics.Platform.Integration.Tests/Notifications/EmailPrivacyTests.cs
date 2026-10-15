using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

public sealed class EmailPrivacyTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private static readonly Dictionary<string, string?> RegistrySettings = new()
	{
		["Registry:AllowSoftwareKeys"] = "true",
		["Registry:RateLimits:PairingCreationPerMinute"] = "10000",
		["Registry:RateLimits:CodeSubmissionPerMinute"] = "10000",
	};

	[Fact]
	// SC-008: no response to a non-owner other than a Club Admin contains an address
	public async Task NoResponseToANonOwnerOtherThanAClubAdminContainsAnAddress()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct, RegistrySettings);
		var adminAddress = EmailApiHarness.NewAddress();
		await h.SetOkAsync(adminAddress, null, ct);
		var registrarId = await TestMembers.SeedAsync(h.Db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		var otherId = await TestMembers.SeedAsync(h.Db, "member-2", cancellationToken: ct);
		var otherAddress = EmailApiHarness.NewAddress();
		var registrarAddress = EmailApiHarness.NewAddress();
		await using (var connection = new NpgsqlConnection(h.Db.AppConnectionString))
		{
			await connection.OpenAsync(ct);
			foreach (var (id, address) in new[] { (otherId, otherAddress), (registrarId, registrarAddress) })
			{
				await using var command = new NpgsqlCommand(
					"INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, verified_at, set_by, updated_at) " +
					"VALUES (@id, @a, @a, 'verified', now(), @id, now())", connection);
				command.Parameters.AddWithValue("id", id);
				command.Parameters.AddWithValue("a", address);
				await command.ExecuteNonQueryAsync(ct);
			}
		}

		using var registrar = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);
		using var member = await ApiSession.SignInAsync(h.Factory, "member-2", TestMembers.DefaultPassword, ct);

		using var manager = new TestAnalystManager();
		using var anonymous = h.Factory.CreateApiClient();
		using var pair = await RegistryTestHost.PairAsync(anonymous, RegistryTestHost.PairingBody(manager), ct);
		var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
		using var submitted = await registrar.SendAsync(
			HttpMethod.Post,
			"/api/v1/analyst-manager-registrations",
			new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() },
			ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Created);

		string[] paths =
		[
			"/api/v1/me",
			"/api/v1/session",
			"/api/v1/members",
			$"/api/v1/members/{otherId}",
			$"/api/v1/members/{registrarId}",
			$"/api/v1/members/{otherId}/email",
			"/api/v1/teams",
			"/api/v1/analyst-manager-registrations",
		];
		var seen = 0;
		foreach (var session in new[] { registrar, member })
		{
			foreach (var path in paths)
			{
				using var response = await session.SendAsync(HttpMethod.Get, path, null, ct);
				var text = await response.Content.ReadAsStringAsync(ct);
				foreach (var address in new[] { adminAddress, otherAddress, registrarAddress })
				{
					text.ShouldNotContain(address, customMessage: $"{path} leaked an address");
				}

				if (response.StatusCode == HttpStatusCode.OK)
				{
					seen++;
				}
			}
		}

		seen.ShouldBeGreaterThan(0);
	}

	[Fact]
	// SC-008: audit events carry only the masked address
	public async Task AuditEventsCarryOnlyMaskedAddresses()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var address = EmailApiHarness.NewAddress();
		var version = await h.SetOkAsync(address, null, ct);
		var changed = EmailApiHarness.NewAddress();
		version = await h.SetOkAsync(changed, version, ct);
		(await h.RemoveAsync(version, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

		await using var connection = new NpgsqlConnection(h.Db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT coalesce(string_agg(details::text, ' '), '') FROM socalytics.security_audit_event WHERE event_type LIKE 'identity.email-address-%'", connection);
		var text = (string)(await command.ExecuteScalarAsync(ct))!;
		text.ShouldContain("maskedAddress");
		text.ShouldNotContain(address);
		text.ShouldNotContain(changed);
		text.ShouldNotContain("@example.test");
	}
}
