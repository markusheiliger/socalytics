using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3.Model;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

/// <summary>
/// Uploads a recording of 5 GiB + 1 MiB in 64 MiB parts (US1 AS11, SC-010). The test runs only when the environment
/// variable SOCALYTICS_RUN_LARGE_UPLOAD_TEST is "true" and needs about 12 GB of free Docker disk and a generous time budget.
/// </summary>
public sealed class LargeRecordingUploadTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const string GateVariable = "SOCALYTICS_RUN_LARGE_UPLOAD_TEST";
	private const long PartSize = 64L * 1024 * 1024;
	private const long TotalSize = 5L * 1024 * 1024 * 1024 + 1024 * 1024;

	private static readonly object ValidMapping = new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 5 } } } };

	private static long LengthOf(int partNumber) => Math.Min(PartSize, TotalSize - (partNumber - 1) * PartSize);

	private static int PartCount => (int)((TotalSize + PartSize - 1) / PartSize);

	// Deterministic, seekless stream: byte i of part n is derived from n and i, so nothing is held in memory.
	private sealed class PartStream(int partNumber, long length) : Stream
	{
		private long _position;

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => length;

		public override long Position { get => _position; set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			var n = (int)Math.Min(buffer.Length, length - _position);
			for (var i = 0; i < n; i++)
			{
				buffer[i] = (byte)(partNumber * 17 + (_position + i) * 7);
			}

			_position += n;
			return n;
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private static Sha256Digest DigestOf(int partNumber)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		using var stream = new PartStream(partNumber, LengthOf(partNumber));
		var buffer = new byte[1024 * 1024];
		int read;
		while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
		{
			hash.AppendData(buffer, 0, read);
		}

		return Sha256Digest.FromBytes(hash.GetHashAndReset());
	}

	[Fact(Timeout = 4 * 60 * 60 * 1000)]
	// US1 AS11, SC-010, R16, REC-R10
	public async Task RecordingSlightlyAboveFiveGibUploadsInSixtyFourMibParts()
	{
		Assert.SkipUnless(
			string.Equals(Environment.GetEnvironmentVariable(GateVariable), "true", StringComparison.Ordinal),
			$"Set {GateVariable}=true to run; the test needs about 12 GB of free Docker disk.");

		var ct = TestContext.Current.CancellationToken;
		var keyPrefix = RecordingTestData.NewKeyPrefix();
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await using var factory = new PlatformApiFactory(
			db,
			recordingUpload: RecordingTestData.WithKeyPrefix(keyPrefix, new Dictionary<string, string?>
			{
				["SessionLifetime"] = "06:00:00",
				["GrantLifetime"] = "06:00:00",
			}),
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

		var digests = Enumerable.Range(1, PartCount).Select(DigestOf).ToArray();
		var expectedDigest = CompositeContentDigest.FromPartDigests(PartSize, digests).ToString();

		using var start = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{hierarchy.MatchId}/upload-sessions")
		{
			Content = JsonContent.Create(new
			{
				recording = new { displayName = "Large recording", contentType = "video/mp4" },
				totalSizeBytes = TotalSize,
				partSizeBytes = PartSize,
				partDigests = digests.Select(d => d.ToString()).ToArray(),
			}),
		};
		start.Headers.Add("X-CSRF-Token", coach.AntiforgeryToken);
		start.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		using var started = await coach.Client.SendAsync(start, ct);
		started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(ct));
		var body = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
		var id = body.GetProperty("session").GetProperty("id").GetString()!;
		var grants = body.GetProperty("grants").GetProperty("parts");
		grants.GetArrayLength().ShouldBe(PartCount);

		using var storage = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		foreach (var grant in grants.EnumerateArray())
		{
			var number = grant.GetProperty("partNumber").GetInt32();
			using var put = new HttpRequestMessage(HttpMethod.Put, grant.GetProperty("url").GetString());
			put.Content = new StreamContent(new PartStream(number, LengthOf(number)), 1024 * 1024);
			foreach (var header in grant.GetProperty("requiredHeaders").EnumerateObject())
			{
				if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
				{
					put.Content.Headers.ContentLength = long.Parse(header.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
				}
				else
				{
					put.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
				}
			}

			using var response = await storage.SendAsync(put, ct);
			response.IsSuccessStatusCode.ShouldBeTrue($"part {number}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
		}

		using var s3 = rustFs.CreateS3Client();
		var uploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = rustFs.Bucket, Prefix = keyPrefix }, ct);
		var upload = uploads.MultipartUploads.ShouldHaveSingleItem();
		var stored = new List<PartDetail>();
		var listRequest = new ListPartsRequest { BucketName = rustFs.Bucket, Key = upload.Key, UploadId = upload.UploadId };
		ListPartsResponse page;
		do
		{
			page = await s3.ListPartsAsync(listRequest, ct);
			stored.AddRange(page.Parts ?? []);
			listRequest.PartNumberMarker = page.NextPartNumberMarker.ToString();
		}
		while (page.IsTruncated == true);

		stored.Count.ShouldBe(PartCount);
		foreach (var part in stored)
		{
			part.Size.ShouldBe(LengthOf(part.PartNumber ?? 0));
		}

		using var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{hierarchy.MatchId}/upload-sessions/{id}/completion") { Content = JsonContent.Create(ValidMapping) };
		complete.Headers.Add("X-CSRF-Token", coach.AntiforgeryToken);
		complete.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		using var completed = await coach.Client.SendAsync(complete, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));
		var version = (await completed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("recordingVersion");
		version.GetProperty("contentDigest").GetString().ShouldBe(expectedDigest);
		version.GetProperty("partSizeBytes").GetInt64().ShouldBe(PartSize);
		version.GetProperty("partCount").GetInt32().ShouldBe(PartCount);
		version.GetProperty("totalSizeBytes").GetInt64().ShouldBe(TotalSize);
	}
}
