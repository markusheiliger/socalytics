using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;

namespace SocAlytics.Platform.Integration.Tests.Recordings.Support;

internal sealed record FinalizationSetup(IsolatedDatabase Db, PlatformApiFactory Factory, ApiSession Admin, ApiSession Coach, Guid CoachId, ClubHierarchy Hierarchy)
	: IAsyncDisposable
{
	public static async Task<FinalizationSetup> StartAsync(
		PostgresContainerFixture postgres, RustFsContainerFixture rustFs, CancellationToken ct, Dictionary<string, string?>? sets = null,
		Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices = null,
		Dictionary<string, string?>? extraSettings = null)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var factory = new PlatformApiFactory(
			db,
			recordingUpload: RecordingTestData.WithKeyPrefix(RecordingTestData.NewKeyPrefix()),
			recordingSets: sets,
			configureServices: configureServices,
			extraSettings: extraSettings,
			objectStorage: new Dictionary<string, string?>
			{
				["ServiceUrl"] = rustFs.ServiceUrl,
				["Region"] = rustFs.Region,
				["AccessKey"] = rustFs.AccessKey,
				["SecretKey"] = rustFs.SecretKey,
				["Bucket"] = rustFs.Bucket,
			});
		factory.Time.Set(DateTimeOffset.UtcNow);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var coachId = await TestMembers.SeedAsync(db, "coach-1", cancellationToken: ct);
		var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var hierarchy = await new ClubHierarchyBuilder(factory, admin).CreateAsync(ct);
		await using (var c = new NpgsqlConnection(db.AppConnectionString))
		{
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)", c);
			cmd.Parameters.AddWithValue("m", coachId);
			cmd.Parameters.AddWithValue("t", hierarchy.TeamId);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		var coach = await ApiSession.SignInAsync(factory, "coach-1", TestMembers.DefaultPassword, ct);
		return new FinalizationSetup(db, factory, admin, coach, coachId, hierarchy);
	}

	public async Task<long> ScalarAsync(string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	public async ValueTask DisposeAsync()
	{
		await Factory.DisposeAsync();
		await Db.DisposeAsync();
	}
}
