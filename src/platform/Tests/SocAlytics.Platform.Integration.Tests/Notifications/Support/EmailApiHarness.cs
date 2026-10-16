using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Email;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;

namespace SocAlytics.Platform.Integration.Tests.Notifications.Support;

internal sealed partial class EmailApiHarness : IAsyncDisposable
{
	public const string AdminName = "admin-1";

	private EmailApiHarness(IsolatedDatabase db, PlatformApiFactory factory, MailpitContainerFixture mailpit, ApiSession admin)
	{
		Db = db;
		Factory = factory;
		Mailpit = mailpit;
		Admin = admin;
	}

	public IsolatedDatabase Db { get; }

	public PlatformApiFactory Factory { get; }

	public MailpitContainerFixture Mailpit { get; }

	public ApiSession Admin { get; }

	public static async Task<EmailApiHarness> StartAsync(
		PostgresContainerFixture postgres,
		MailpitContainerFixture mailpit,
		CancellationToken ct,
		IReadOnlyDictionary<string, string?>? extraSettings = null)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var adminId = await TestMembers.SeedAsync(db, AdminName, clubRoles: ["club-admin"], cancellationToken: ct);
		await using (var connection = new Npgsql.NpgsqlConnection(db.AppConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new Npgsql.NpgsqlCommand(
				$"INSERT INTO socalytics.club (id, display_name, bootstrap_admin_account_id, created_at) VALUES ('{Guid.NewGuid()}', 'Example FC', '{adminId}', now())", connection);
			await command.ExecuteNonQueryAsync(ct);
		}

		var settings = new Dictionary<string, string?>
		{
			["Email:Smtp:Host"] = mailpit.SmtpHost,
			["Email:Smtp:Port"] = mailpit.SmtpPort.ToString(),
			["Email:Delivery:PollInterval"] = "00:00:01",
		};
		foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
		{
			settings[key] = value;
		}

		var factory = new PlatformApiFactory(db, extraSettings: settings);
		var admin = await ApiSession.SignInAsync(factory, AdminName, TestMembers.DefaultPassword, ct);
		return new EmailApiHarness(db, factory, mailpit, admin);
	}

	public static string NewAddress() => $"member-{Guid.NewGuid():N}@example.test";

	public async Task<HttpResponseMessage> SetAsync(string address, long? version, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/email") { Content = JsonContent.Create(new { address }) };
		request.Headers.Add("X-CSRF-Token", Admin.AntiforgeryToken);
		if (version is { } v)
		{
			request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
		}

		return await Admin.Client.SendAsync(request, ct);
	}

	public async Task<HttpResponseMessage> RemoveAsync(long version, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me/email");
		request.Headers.Add("X-CSRF-Token", Admin.AntiforgeryToken);
		request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
		return await Admin.Client.SendAsync(request, ct);
	}

	public Task<HttpResponseMessage> ResendAsync(CancellationToken ct) =>
		Admin.SendAsync(HttpMethod.Post, "/api/v1/me/email/confirmation-requests", null, ct);

	public async Task<JsonElement> GetOwnAsync(CancellationToken ct)
	{
		using var response = await Admin.SendAsync(HttpMethod.Get, "/api/v1/me/email", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
	}

	public async Task<long> SetOkAsync(string address, long? version, CancellationToken ct)
	{
		using var response = await SetAsync(address, version, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		body.GetProperty("state").GetString().ShouldBe("unverified");
		return body.GetProperty("version").GetInt64();
	}

	public async Task<HttpResponseMessage> ConfirmAsync(string? token, CancellationToken ct)
	{
		using var anonymous = Factory.CreateApiClient();
		return await anonymous.PostAsJsonAsync("/api/v1/email-confirmations", new { token }, ct);
	}

	// The worker loop runs on the frozen test clock, so the tests deliver explicitly.
	public async Task DeliverAsync(CancellationToken ct)
	{
		int claimed;
		do
		{
			await using var scope = Factory.Services.CreateAsyncScope();
			claimed = await scope.ServiceProvider.GetRequiredService<EmailDeliveryProcessor>().ProcessDueAsync(ct);
		}
		while (claimed >= EmailDeliveryProcessor.BatchSize);
	}

	public async Task<string> TokenFromMailAsync(string address, int count, CancellationToken ct)
	{
		await DeliverAsync(ct);
		var found = await Mailpit.WaitForMessagesAsync($"to:{address}", count, TimeSpan.FromSeconds(30), ct);
		var text = await Mailpit.GetTextBodyAsync(found[0].Id, ct);
		var match = TokenPattern().Match(text);
		match.Success.ShouldBeTrue();
		return match.Groups[1].Value;
	}

	public async Task<int> MailCountAsync(string address, CancellationToken ct)
	{
		await DeliverAsync(ct);
		await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
		return (await Mailpit.SearchAsync($"to:{address}", ct)).Count;
	}

	public async ValueTask DisposeAsync()
	{
		Admin.Dispose();
		await Factory.DisposeAsync();
		await Db.DisposeAsync();
	}

	[GeneratedRegex(@"/email/confirm#token=([A-Za-z0-9_-]+)")]
	private static partial Regex TokenPattern();
}
