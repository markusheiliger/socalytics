using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class RecordingRetryOutcomeStoreTests(PostgresContainerFixture postgres)
{
	private static readonly string Digest = "sha-256:" + new string('a', 64);

	[Fact]
	public void CanonicalJsonOrdersPropertiesAndOmitsWhitespace()
	{
		var a = JsonNode.Parse("""{"b":1,"a":{"d":[3,{"z":1,"y":2}],"c":"x"}}""");
		var b = JsonNode.Parse("""{ "a": { "c": "x", "d": [3, {"y":2,"z":1}] }, "b": 1 }""");
		System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(a))
			.ShouldBe("""{"a":{"c":"x","d":[3,{"y":2,"z":1}]},"b":1}""");
		CanonicalJson.Digest(a).ShouldBe(CanonicalJson.Digest(b));
	}

	[Fact]
	public void DigestDependsOnOperationRouteAndBody()
	{
		var route = new Dictionary<string, string> { ["matchId"] = "m1" };
		var body = JsonNode.Parse("""{"p":1}""");
		var baseline = CanonicalJson.Digest(RecordingRetryOperation.StartUpload, route, body);
		CanonicalJson.Digest(RecordingRetryOperation.StartUpload, route, body).ShouldBe(baseline);
		CanonicalJson.Digest(RecordingRetryOperation.CompleteUpload, route, body).ShouldNotBe(baseline);
		CanonicalJson.Digest(RecordingRetryOperation.StartUpload, new Dictionary<string, string> { ["matchId"] = "m2" }, body).ShouldNotBe(baseline);
		CanonicalJson.Digest(RecordingRetryOperation.StartUpload, route, JsonNode.Parse("""{"p":2}""")).ShouldNotBe(baseline);
	}

	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Retry Club",
		["FirstClubAdmin:AccountName"] = "retry-bootstrap",
		["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
	};

	private async Task<(IsolatedDatabase Db, Guid Match1, Guid Match2, Guid Account)> SeedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await using var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);
		var account = await TestMembers.SeedAsync(db, "retry-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "retry-admin", TestMembers.DefaultPassword, ct);
		var builder = new ClubHierarchyBuilder(factory, admin);
		var hierarchy = await builder.CreateAsync(ct);
		var second = await builder.CreateMatchAsync(hierarchy.TeamId, ct);
		return (db, hierarchy.MatchId, second, account);
	}

	private static async Task InsertAsync(IServiceProvider root, Func<IRecordingRetryOutcomeStore, Task> action, bool commit, CancellationToken ct)
	{
		await using var scope = root.CreateAsyncScope();
		await using var work = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await action(scope.ServiceProvider.GetRequiredService<IRecordingRetryOutcomeStore>());
		if (commit)
		{
			await work.CommitAsync(ct);
		}
	}

	private static async Task<RecordingRetryOutcome?> FindAsync(
		IServiceProvider root, RecordingRetryOperation op, Guid match, string key, CancellationToken ct)
	{
		await using var scope = root.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<IRecordingRetryOutcomeStore>().FindAsync(op, match, key, ct);
	}

	[Fact]
	public async Task InsertFindDuplicateIndependenceAndRollback()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, match1, match2, account) = await SeedAsync(ct);
		await using var _ = db;
		await using var provider = PlatformServices.Build(db);
		var now = DateTimeOffset.UtcNow;
		var result = $$"""{"uploadSessionId":"{{Guid.NewGuid()}}"}""";
		var op = RecordingRetryOperation.StartUpload;

		await InsertAsync(provider, s => s.InsertAsync(op, match1, "k1", Digest, 201, result, account, now, ct), true, ct);
		var found = await FindAsync(provider, op, match1, "k1", ct);
		found.ShouldNotBeNull();
		found.RequestDigest.ShouldBe(Digest);
		found.StatusCode.ShouldBe(201);
		JsonNode.Parse(found.Result)!["uploadSessionId"]!.ToString().ShouldBe(JsonNode.Parse(result)!["uploadSessionId"]!.ToString());

		await Should.ThrowAsync<DuplicateRetryKeyException>(() =>
			InsertAsync(provider, s => s.InsertAsync(op, match1, "k1", Digest, 201, result, account, now, ct), true, ct));

		await InsertAsync(provider, s => s.InsertAsync(op, match2, "k1", Digest, 201, result, account, now, ct), true, ct);
		await InsertAsync(provider, s => s.InsertAsync(RecordingRetryOperation.CompleteUpload, match1, "k1", Digest, 200, result, account, now, ct), true, ct);

		await InsertAsync(provider, s => s.InsertAsync(op, match1, "k2", Digest, 201, result, account, now, ct), false, ct);
		(await FindAsync(provider, op, match1, "k2", ct)).ShouldBeNull();
	}
}
