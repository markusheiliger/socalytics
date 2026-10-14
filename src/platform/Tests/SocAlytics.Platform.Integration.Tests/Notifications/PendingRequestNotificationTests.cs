using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

public sealed class PendingRequestNotificationTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string Registrations = "/api/v1/analyst-manager-registrations";

	private static readonly Dictionary<string, string?> RegistrySettings = new()
	{
		["Registry:AllowSoftwareKeys"] = "true",
		["Registry:RateLimits:PairingCreationPerMinute"] = "10000",
		["Registry:RateLimits:CodeSubmissionPerMinute"] = "10000",
	};

	private static async Task ExecuteAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		await command.ExecuteNonQueryAsync(ct);
	}

	private static Task VerifyAddressAsync(IsolatedDatabase db, Guid accountId, string address, CancellationToken ct) =>
		ExecuteAsync(
			db,
			"INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, verified_at, set_by, updated_at) " +
			"VALUES (@id, @a, @a, 'verified', now(), @id, now())",
			ct, ("id", accountId), ("a", address));

	private static Task SetPreferenceAsync(IsolatedDatabase db, Guid accountId, bool enabled, CancellationToken ct) =>
		ExecuteAsync(
			db,
			"INSERT INTO socalytics.notification_preferences (account_id, notification_type, enabled, updated_at) " +
			"VALUES (@id, 'registration-pending', @e, now()) ON CONFLICT (account_id, notification_type) DO UPDATE SET enabled = @e",
			ct, ("id", accountId), ("e", enabled));

	private static async Task<(string Code, string Fingerprint)> PairAsync(EmailApiHarness h, CancellationToken ct)
	{
		using var manager = new TestAnalystManager();
		using var anonymous = h.Factory.CreateApiClient();
		using var pair = await RegistryTestHost.PairAsync(anonymous, RegistryTestHost.PairingBody(manager), ct);
		pair.StatusCode.ShouldBe(HttpStatusCode.Created);
		var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
		return (root.GetProperty("pairingCode").GetString()!, root.GetProperty("deviceFingerprint").GetString()!);
	}

	private static async Task<string> SubmitAsync(EmailApiHarness h, ApiSession registrar, CancellationToken ct)
	{
		var (code, fingerprint) = await PairAsync(h, ct);
		using var submitted = await registrar.SendAsync(
			HttpMethod.Post, Registrations, new { pairingCode = code, deviceFingerprint = fingerprint }, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync(ct));
		return fingerprint;
	}

	private static string Address() => EmailApiHarness.NewAddress();

	private async Task<int> CountAsync(EmailApiHarness h, string address, CancellationToken ct)
	{
		await h.DeliverAsync(ct);
		await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
		return (await mailpit.SearchAsync($"to:{address}", ct)).Count;
	}

	[Fact]
	// US2 scenarios 1, 2, 3, and 5; SC-002 and SC-004
	public async Task OnlyEligibleClubAdminsReceiveOneMessageWithOnlyTheEventAndTheLink()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct, RegistrySettings);
		var db = h.Db;
		var verified = Address();
		var unverified = Address();
		var optedOut = Address();
		var deactivated = Address();
		var coach = Address();

		var other = await TestMembers.SeedAsync(db, "admin-2", clubRoles: ["club-admin"], cancellationToken: ct);
		var noAddress = await TestMembers.SeedAsync(db, "admin-3", clubRoles: ["club-admin"], cancellationToken: ct);
		var off = await TestMembers.SeedAsync(db, "admin-4", clubRoles: ["club-admin"], cancellationToken: ct);
		var gone = await TestMembers.SeedAsync(db, "admin-5", clubRoles: ["club-admin"], cancellationToken: ct);
		var plain = await TestMembers.SeedAsync(db, "coach-1", clubRoles: ["registrar"], cancellationToken: ct);
		await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		noAddress.ShouldNotBe(Guid.Empty);

		var adminId = await GetAccountIdAsync(db, EmailApiHarness.AdminName, ct);
		await VerifyAddressAsync(db, adminId, verified, ct);
		await ExecuteAsync(
			db,
			"INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, set_by, updated_at) " +
			"VALUES (@id, @a, @a, 'unverified', @id, now())",
			ct, ("id", other), ("a", unverified));
		await VerifyAddressAsync(db, off, optedOut, ct);
		await SetPreferenceAsync(db, off, false, ct);
		await VerifyAddressAsync(db, gone, deactivated, ct);
		await ExecuteAsync(db, "UPDATE socalytics.member_account SET membership_status = 'deactivated' WHERE id = @id", ct, ("id", gone));
		await VerifyAddressAsync(db, plain, coach, ct);

		using var registrar = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);
		var fingerprint = await SubmitAsync(h, registrar, ct);

		(await CountAsync(h, verified, ct)).ShouldBe(1);
		foreach (var silent in new[] { unverified, optedOut, deactivated, coach })
		{
			(await CountAsync(h, silent, ct)).ShouldBe(0);
		}

		var message = (await mailpit.SearchAsync($"to:{verified}", ct)).Single();
		message.Subject.ShouldBe("An Analyst Manager registration request waits for approval");
		var text = await mailpit.GetTextBodyAsync(message.Id, ct);
		text.ShouldContain("one or more", Case.Insensitive);
		text.ShouldContain("http://localhost:5173/analyst-managers");
		text.ShouldNotContain("#token");
		text.ShouldNotContain(fingerprint);
		text.ShouldNotContain("reg-1");
		text.ShouldNotContain("local-dev-stamp");
	}

	[Fact]
	// US2 scenario 4: coalescing, and a discarded message not suppressing the next one
	public async Task FurtherRequestsInTheWindowAreCoalescedAndDiscardedMessagesDoNotCount()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct, RegistrySettings);
		var address = Address();
		var adminId = await GetAccountIdAsync(h.Db, EmailApiHarness.AdminName, ct);
		await VerifyAddressAsync(h.Db, adminId, address, ct);
		await TestMembers.SeedAsync(h.Db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var registrar = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);

		await SubmitAsync(h, registrar, ct);
		await SubmitAsync(h, registrar, ct);
		await SubmitAsync(h, registrar, ct);
		(await CountAsync(h, address, ct)).ShouldBe(1);

		h.Factory.Time.Advance(TimeSpan.FromMinutes(16));
		using var later = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);
		await SubmitAsync(h, later, ct);
		(await CountAsync(h, address, ct)).ShouldBe(2);

		// The next message is discarded at send time because the recipient opted out meanwhile.
		h.Factory.Time.Advance(TimeSpan.FromMinutes(16));
		using var third = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);
		await SubmitAsync(h, third, ct);
		await SetPreferenceAsync(h.Db, adminId, false, ct);
		(await CountAsync(h, address, ct)).ShouldBe(2);
		await SetPreferenceAsync(h.Db, adminId, true, ct);

		await SubmitAsync(h, third, ct);
		(await CountAsync(h, address, ct)).ShouldBe(3);
	}

	[Fact]
	// US2 scenario 6: the recipient loses the role before the send
	public async Task AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct, RegistrySettings);
		var address = Address();
		var admin = await TestMembers.SeedAsync(h.Db, "admin-2", clubRoles: ["club-admin"], cancellationToken: ct);
		await VerifyAddressAsync(h.Db, admin, address, ct);
		await TestMembers.SeedAsync(h.Db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var registrar = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);

		await SubmitAsync(h, registrar, ct);
		await ExecuteAsync(h.Db, "DELETE FROM socalytics.club_role_assignment WHERE member_account_id = @id", ct, ("id", admin));

		(await CountAsync(h, address, ct)).ShouldBe(0);
	}

	[Fact]
	// Two concurrent submissions produce one message per Club Admin
	public async Task ConcurrentSubmissionsProduceOneMessagePerClubAdmin()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct, RegistrySettings);
		var first = Address();
		var second = Address();
		var adminId = await GetAccountIdAsync(h.Db, EmailApiHarness.AdminName, ct);
		var admin2 = await TestMembers.SeedAsync(h.Db, "admin-2", clubRoles: ["club-admin"], cancellationToken: ct);
		await VerifyAddressAsync(h.Db, adminId, first, ct);
		await VerifyAddressAsync(h.Db, admin2, second, ct);
		await TestMembers.SeedAsync(h.Db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		await TestMembers.SeedAsync(h.Db, "reg-2", clubRoles: ["registrar"], cancellationToken: ct);
		using var reg1 = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);
		using var reg2 = await ApiSession.SignInAsync(h.Factory, "reg-2", TestMembers.DefaultPassword, ct);

		await Task.WhenAll(SubmitAsync(h, reg1, ct), SubmitAsync(h, reg2, ct));

		(await CountAsync(h, first, ct)).ShouldBe(1);
		(await CountAsync(h, second, ct)).ShouldBe(1);
	}

	private static async Task<Guid> GetAccountIdAsync(IsolatedDatabase db, string name, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT id FROM socalytics.member_account WHERE account_name = @n", connection);
		command.Parameters.AddWithValue("n", name);
		return (Guid)(await command.ExecuteScalarAsync(ct))!;
	}
}
