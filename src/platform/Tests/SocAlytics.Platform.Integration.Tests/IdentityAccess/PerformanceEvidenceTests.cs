using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

// These are local development measurements, not production evidence. Results from CI runners
// (shared hardware, other test assemblies running) are indicative only.
[Collection(TimingCollection.Name)]
public sealed class PerformanceEvidenceTests(PostgresContainerFixture postgres, ITestOutputHelper output)
{
	private const int Iterations = 100;
	private const string AdminPassword = "Perf-Admin-Pass-1234";
	private const string BootPassword = "Perf-Boot-Pass-1234";
	private const string ChangedPassword = "Perf-Changed-Pass-5678";
	private const string MemberPassword = "Perf-Member-Pass-9012";

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(expected, text);
		return text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default;
	}

	private static async Task<HttpResponseMessage> SendAsync(
		ApiSession session, HttpMethod method, string path, object? body, CancellationToken ct, string? ifMatch = null)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await session.Client.SendAsync(request, ct);
	}

	private double Percentile95(string name, List<double> samples)
	{
		var sorted = samples.OrderBy(v => v).ToList();
		var p95 = sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
		output.WriteLine($"{name}: n={sorted.Count} median={sorted[sorted.Count / 2]:F1}ms p95={p95:F1}ms max={sorted[^1]:F1}ms");
		return p95;
	}

	[Fact]
	public async Task CoreOperationsHaveSubSecondP95()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "perf-admin", password: AdminPassword, clubRoles: ["club-admin"], cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);

		using var admin = await ApiSession.SignInAsync(factory, "perf-admin", AdminPassword, ct);
		var season = (await ReadAsync(await SendAsync(admin, HttpMethod.Post, "/api/v1/seasons", new { name = "Perf" }, ct), HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
		var team = (await ReadAsync(await SendAsync(admin, HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name = "Perf A" }, ct), HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();

		// warm-up
		for (var i = 0; i < 5; i++)
		{
			using var warm = await ApiSession.SignInAsync(factory, "perf-admin", AdminPassword, ct);
			using var _ = await SendAsync(warm, HttpMethod.Delete, "/api/v1/session", null, ct);
		}

		var signIn = new List<double>();
		var signOut = new List<double>();
		for (var i = 0; i < Iterations; i++)
		{
			var client = factory.CreateApiClient();
			var started = Stopwatch.GetTimestamp();
			using var response = await client.PostAsJsonAsync("/api/v1/session", new { accountName = "perf-admin", password = AdminPassword }, ct);
			signIn.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
			var info = await ReadAsync(response, HttpStatusCode.OK, ct);
			var token = info.GetProperty("antiforgeryToken").GetString()!;

			using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/session");
			request.Headers.Add("X-CSRF-Token", token);
			started = Stopwatch.GetTimestamp();
			using var signedOut = await client.SendAsync(request, ct);
			signOut.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
			signedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
			client.Dispose();
		}

		var getTeam = new List<double>();
		var updateTeam = new List<double>();
		string etag = null!;
		for (var i = 0; i < Iterations; i++)
		{
			var started = Stopwatch.GetTimestamp();
			using var get = await admin.Client.GetAsync($"/api/v1/teams/{team}", ct);
			getTeam.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
			get.StatusCode.ShouldBe(HttpStatusCode.OK);
			etag = get.Headers.ETag!.Tag;

			started = Stopwatch.GetTimestamp();
			using var put = await SendAsync(admin, HttpMethod.Put, $"/api/v1/teams/{team}", new { name = $"Perf A {i}" }, ct, etag);
			updateTeam.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
			put.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		Percentile95("sign-in", signIn).ShouldBeLessThan(1000);
		Percentile95("sign-out", signOut).ShouldBeLessThan(1000);
		Percentile95("getTeam", getTeam).ShouldBeLessThan(1000);
		Percentile95("updateTeam", updateTeam).ShouldBeLessThan(1000);
	}

	[Fact]
	public async Task FirstAdminSetupAndClubOnboardingAreFast()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db, new Dictionary<string, string?>
		{
			["ClubDisplayName"] = "Perf Club",
			["FirstClubAdmin:AccountName"] = "perf-boot",
			["FirstClubAdmin:InitialPassword"] = BootPassword,
		});

		await factory.WaitUntilHealthyAsync(ct);
		var setup = Stopwatch.StartNew();
		using (var first = await ApiSession.SignInAsync(factory, "perf-boot", BootPassword, ct))
		{
			first.Info.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
			using var change = await SendAsync(first, HttpMethod.Post, "/api/v1/me/password",
				new { currentPassword = BootPassword, newPassword = ChangedPassword }, ct);
			change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		using var admin = await ApiSession.SignInAsync(factory, "perf-boot", ChangedPassword, ct);
		var me = await ReadAsync(await admin.Client.GetAsync("/api/v1/me", ct), HttpStatusCode.OK, ct);
		me.GetProperty("clubRoles").EnumerateArray().Select(r => r.GetString()).ShouldContain("club-admin");
		setup.Stop();
		output.WriteLine($"SC-001 first Club Admin setup: {setup.Elapsed.TotalSeconds:F2}s");
		setup.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(2));

		var onboarding = Stopwatch.StartNew();
		var season = (await ReadAsync(await SendAsync(admin, HttpMethod.Post, "/api/v1/seasons", new { name = "Onboarding" }, ct), HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
		var team = (await ReadAsync(await SendAsync(admin, HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name = "Onboarding A" }, ct), HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
		var created = await ReadAsync(await SendAsync(admin, HttpMethod.Post, "/api/v1/members", new { accountName = "perf-coach" }, ct), HttpStatusCode.Created, ct);
		var memberId = created.GetProperty("member").GetProperty("id").GetGuid();
		var credential = created.GetProperty("setPasswordCredential").GetProperty("credential").GetString()!;
		using (var client = factory.CreateApiClient())
		{
			using var redeem = await client.PostAsJsonAsync("/api/v1/credentials/redeem",
				new { accountName = "perf-coach", credential, newPassword = MemberPassword }, ct);
			redeem.IsSuccessStatusCode.ShouldBeTrue();
		}

		using var assign = await SendAsync(admin, HttpMethod.Put, $"/api/v1/members/{memberId}/team-roles/{team}", new { role = "coach" }, ct);
		assign.StatusCode.ShouldBe(HttpStatusCode.OK);
		onboarding.Stop();
		output.WriteLine($"SC-002 season, team, member, Coach assignment: {onboarding.Elapsed.TotalSeconds:F2}s");
		onboarding.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(5));
	}
}
