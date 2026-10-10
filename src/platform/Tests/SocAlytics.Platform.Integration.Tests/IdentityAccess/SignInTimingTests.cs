using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class KolmogorovSmirnovTests
{
	[Fact]
	public void StatisticOfDisjointSamplesIsOne() =>
		KolmogorovSmirnov.Statistic([1, 2, 3, 4], [5, 6, 7, 8]).ShouldBe(1.0);

	[Fact]
	public void StatisticOfIdenticalSamplesIsZero() =>
		KolmogorovSmirnov.Statistic([1, 2, 3, 4], [1, 2, 3, 4]).ShouldBe(0.0);

	[Fact]
	public void StatisticOfPartiallyOverlappingSamples() =>
		KolmogorovSmirnov.Statistic([1, 2, 3, 4], [3, 4, 5, 6]).ShouldBe(0.5, 1e-12);

	[Fact]
	public void CriticalCoefficientsMatchTables()
	{
		KolmogorovSmirnov.Coefficient(0.01).ShouldBe(1.628, 0.001);
		KolmogorovSmirnov.Coefficient(0.00025).ShouldBe(2.120, 0.001);
	}
}

[Collection(TimingCollection.Name)]
public sealed class SignInTimingTests(PostgresContainerFixture postgres, ITestOutputHelper output)
{
	private const double Alpha = 0.001 / 4;
	private const int Warmup = 20;
	private const int Samples = 200;
	private static readonly string[] Classes = ["wrong-password", "unknown", "no-password", "locked", "inactive"];

	[Fact]
	// Quickstart A55
	public async Task RefusalClassesHaveIndistinguishableTimingDistributions()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await TestMembers.SeedAsync(db, "wrong-pw", cancellationToken: ct);
		await TestMembers.SeedAsync(db, "no-pw", password: null, cancellationToken: ct);
		await TestMembers.SeedAsync(db, "locked-one", lockedOut: true, cancellationToken: ct);
		await TestMembers.SeedAsync(db, "inactive-one", membershipStatus: "deactivated", cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);
		using var client = factory.CreateApiClient();

		var names = new Dictionary<string, string>
		{
			["wrong-password"] = "wrong-pw",
			["unknown"] = "nobody-here",
			["no-password"] = "no-pw",
			["locked"] = "locked-one",
			["inactive"] = "inactive-one",
		};

		var first = await CollectAsync(client, db, names, Samples, ct);
		var failures = Compare("first sample", first);
		if (failures.Count > 0)
		{
			output.WriteLine("Significant difference in the first sample; collecting one fresh independent sample.");
			var second = await CollectAsync(client, db, names, Samples, ct);
			failures = Compare("re-sample", second);
		}

		failures.ShouldBeEmpty("Sign-in refusal timing differs from the wrong-password class: " + string.Join(", ", failures));

		var lockout = await ScalarAsync(db, "SELECT lockout_end > now() FROM socalytics.member_account WHERE account_name = 'locked-one'", ct);
		lockout.ShouldBe(true);
	}

	private List<string> Compare(string label, Dictionary<string, List<double>> data)
	{
		var failed = new List<string>();
		var reference = data["wrong-password"];
		foreach (var cls in Classes.Skip(1))
		{
			var d = KolmogorovSmirnov.Statistic(reference, data[cls]);
			var critical = KolmogorovSmirnov.CriticalValue(Alpha, reference.Count, data[cls].Count);
			var significant = d > critical;
			output.WriteLine(
				$"{label}: {cls} vs wrong-password D={d:F4} critical={critical:F4} " +
				$"median={Median(data[cls]):F2}ms reference median={Median(reference):F2}ms significant={significant}");
			if (significant)
			{
				failed.Add(cls);
			}
		}

		return failed;
	}

	private static double Median(List<double> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);

	private static async Task<Dictionary<string, List<double>>> CollectAsync(
		HttpClient client,
		IsolatedDatabase db,
		Dictionary<string, string> names,
		int count,
		CancellationToken ct)
	{
		var random = new Random();
		var result = Classes.ToDictionary(c => c, _ => new List<double>());

		for (var i = 0; i < Warmup; i++)
		{
			foreach (var cls in Classes.OrderBy(_ => random.Next()))
			{
				await AttemptAsync(client, names[cls], ct);
			}
		}

		for (var round = 0; round < count; round++)
		{
			// Keeps the wrong-password account below the lockout threshold.
			await ScalarAsync(db, "UPDATE socalytics.member_account SET access_failed_count = 0 WHERE account_name = 'wrong-pw' AND lockout_end IS NULL RETURNING 1", ct);
			foreach (var cls in Classes.OrderBy(_ => random.Next()))
			{
				result[cls].Add(await AttemptAsync(client, names[cls], ct));
			}
		}

		return result;
	}

	private static async Task<double> AttemptAsync(HttpClient client, string name, CancellationToken ct)
	{
		var started = Stopwatch.GetTimestamp();
		using var response = await client.PostAsJsonAsync("/api/v1/session", new { accountName = name, password = "not-the-password" }, ct);
		var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		return elapsed;
	}

	private static async Task<object?> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(db.AppConnectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand(sql, c);
		var value = await cmd.ExecuteScalarAsync(ct);
		return value is DBNull ? null : value;
	}
}
