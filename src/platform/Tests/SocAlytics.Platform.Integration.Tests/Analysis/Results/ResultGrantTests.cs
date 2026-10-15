using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class ResultGrantTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private sealed record Claimed(Guid Job, Guid Attempt, long Token, DateTimeOffset LeaseExpiresAt);

    private static string DigestOf(string text) => "sha-256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static object Obj(string name, long size = 100, string? digest = null) => new { name, sizeBytes = size, digest = digest ?? DigestOf(name) };

    private static object Request(long token, params object[] objects) => new { fencingToken = token, objects };

    private static async Task<string?> TextAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (await cmd.ExecuteScalarAsync(ct))?.ToString();
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(ManagerAuthentication auth, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: auth);
        await host.StartAsync(ct);
        return (db, host);
    }

    private static async Task<Claimed> ClaimAsync(IsolatedDatabase db, TestManagerClient manager, CancellationToken ct)
    {
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        using var response = await manager.ClaimAsync(job, occ, "key-" + job, ct: ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var a = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        return new Claimed(job, a.GetProperty("attemptId").GetGuid(), a.GetProperty("fencingToken").GetInt64(), a.GetProperty("leaseExpiresAt").GetDateTimeOffset());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task IssuesGrantsForManifestAndArtifactUnderTheAttemptLocation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(db, m, ct);

        using var response = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("manifest.json", 2048), Obj("facts.json", 10240)), ct);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var (status, body) = await ReadAsync(response, ct);
        status.ShouldBe(HttpStatusCode.OK, body.ToString());

        var validator = host.Services.GetRequiredService<IContractValidator>();
        var validation = validator.Validate(ContractSchemaIds.ResultGrantsV1, body);
        validation.IsValid.ShouldBeTrue(string.Join("; ", validation.Errors.Select(e => e.Field + ":" + e.Keyword)));

        var runId = await TextAsync(db, "SELECT run_id::text FROM socalytics.analysis_logical_jobs", ct);
        body.GetProperty("resultLocation").GetString().ShouldBe($"results/{runId}/{c.Job}/{c.Attempt}/");
        var grants = body.GetProperty("grants").EnumerateArray().ToList();
        grants.Select(g => g.GetProperty("name").GetString()).ShouldBe(["manifest.json", "facts.json"]);
        foreach (var g in grants)
        {
            g.GetProperty("method").GetString().ShouldBe("PUT");
            g.GetProperty("url").GetString()!.ShouldContain($"{c.Attempt}/{g.GetProperty("name").GetString()}");
            g.GetProperty("requiredHeaders").GetProperty("x-amz-checksum-sha256").GetString().ShouldNotBeNullOrEmpty();
        }

        grants[0].GetProperty("requiredHeaders").GetProperty("Content-Length").GetString().ShouldBe("2048");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_result_object_declarations", ct)).ShouldBe("2");
    }

    [Fact]
    public async Task RepeatedDeclarationWithSameDigestIsIdempotentAndDifferentDigestIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(db, m, ct);

        using var first = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("facts.json")), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var again = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("facts.json")), ct);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_result_object_declarations", ct)).ShouldBe("1");

        using var changed = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("facts.json", 100, DigestOf("other"))), ct);
        var (status, body) = await ReadAsync(changed, ct);
        status.ShouldBe(HttpStatusCode.Conflict);
        body.GetProperty("code").GetString().ShouldBe("result-object-redeclared");
        (await TextAsync(db, "SELECT digest FROM socalytics.analysis_result_object_declarations", ct)).ShouldBe(DigestOf("facts.json"));
    }

    [Fact]
    public async Task InvalidObjectDeclarationsAreRefusedWithoutDeclaringAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(db, m, ct);

        var cases = new Dictionary<string, object>
        {
            ["duplicate"] = Request(c.Token, Obj("a.json"), Obj("a.json")),
            ["uppercase"] = Request(c.Token, Obj("A.json")),
            ["path"] = Request(c.Token, Obj("../a.json")),
            ["leading-dot"] = Request(c.Token, Obj(".a.json")),
            ["bad-digest"] = Request(c.Token, Obj("a.json", 10, "sha-256:xyz")),
            ["size-zero"] = Request(c.Token, Obj("a.json", 0)),
            ["too-large"] = Request(c.Token, Obj("a.json", 104857601)),
            ["manifest-too-large"] = Request(c.Token, Obj("manifest.json", 1048577)),
            ["too-many"] = Request(c.Token, Enumerable.Range(0, 17).Select(i => Obj($"o{i}.json")).ToArray()),
            ["too-many-bytes"] = Request(c.Token, Enumerable.Range(0, 11).Select(i => Obj($"o{i}.json", 104857600)).ToArray()),
            ["empty"] = Request(c.Token),
        };
        foreach (var (name, request) in cases)
        {
            using var response = await m.RequestResultGrantsAsync(c.Attempt, request, ct);
            var (status, body) = await ReadAsync(response, ct);
            status.ShouldBe(HttpStatusCode.BadRequest, name);
            body.GetProperty("code").GetString().ShouldBe("result-objects-invalid", name);
        }

        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_result_object_declarations", ct)).ShouldBe("0");
    }

    [Fact]
    public async Task ForeignUnknownFencedAndCompletedAttemptsAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        using var m = new TestManagerClient(host.Factory);
        using var other = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(db, m, ct);
        var request = Request(c.Token, Obj("a.json"));

        using var foreign = await other.RequestResultGrantsAsync(c.Attempt, request, ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var unknown = await m.RequestResultGrantsAsync(Guid.NewGuid(), request, ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var wrongToken = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token + 1, Obj("a.json")), ct);
        var (status, body) = await ReadAsync(wrongToken, ct);
        status.ShouldBe(HttpStatusCode.Conflict);
        body.GetProperty("code").GetString().ShouldBe("attempt-obsolete");

        (await TextAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-grant-refused'", ct)).ShouldBe("1");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_result_object_declarations", ct)).ShouldBe("0");

        await using (var conn = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "UPDATE socalytics.analysis_execution_attempts SET state = 'Stale', closed_at = now(), close_reason = 'lease-expired'; " +
                "UPDATE socalytics.analysis_logical_jobs SET current_attempt_id = NULL", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var fenced = await m.RequestResultGrantsAsync(c.Attempt, request, ct);
        fenced.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GrantExpiryNeverExceedsTheCurrentLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(db, m, ct);

        using var response = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("a.json")), ct);
        var (_, body) = await ReadAsync(response, ct);
        var expires = body.GetProperty("grants")[0].GetProperty("expiresAt").GetDateTimeOffset();
        expires.ShouldBeLessThanOrEqualTo(c.LeaseExpiresAt.AddSeconds(1));
        expires.ShouldBeGreaterThan(host.Time.GetUtcNow());

        host.Time.Advance(TimeSpan.FromMinutes(5));
        using var late = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("a.json")), ct);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UnregisteringManagerObtainsGrantsAndRevokedManagerIsRefusedByAuthentication()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Real, ct);
        await using var _ = host;
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        using var manager = await RegisteredAnalystManagerClient.RegisterAsync(host, db, ct);
        using var claim = await manager.ClaimAsync(job, occ, "real-1", ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created);
        var a = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var path = $"/api/v1/analysis/attempts/{a.GetProperty("attemptId").GetGuid()}/result-grants";
        var request = Request(a.GetProperty("fencingToken").GetInt64(), Obj("manifest.json", 2048));

        using var begin = await manager.SendAsync(HttpMethod.Post, "/api/v1/analyst-managers/self/unregistration", null, null, ct);
        begin.StatusCode.ShouldBe(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync(ct));
        using var draining = await manager.SendAsync(HttpMethod.Post, path, request, null, ct);
        draining.StatusCode.ShouldBe(HttpStatusCode.OK, await draining.Content.ReadAsStringAsync(ct));

        await manager.RevokeAsync(ct);
        using var revoked = await manager.SendAsync(HttpMethod.Post, path, request, null, ct);
        revoked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoLogOrAuditRecordContainsGrantUrlsOrSignatures()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        using var m = new TestManagerClient(host.Factory);
        using var other = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(db, m, ct);

        using var response = await m.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("manifest.json", 2048)), ct);
        var (_, body) = await ReadAsync(response, ct);
        var url = body.GetProperty("grants")[0].GetProperty("url").GetString()!;
        url.ShouldContain("X-Amz-Signature");
        using var foreign = await other.RequestResultGrantsAsync(c.Attempt, Request(c.Token, Obj("manifest.json", 2048)), ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var logs = host.Factory.Logs.Entries.ToList();
        logs.ShouldContain(e => e.Message.Contains("result grants for attempt") && e.Message.Contains("manifest.json"));
        foreach (var entry in logs)
        {
            var text = entry.Message + entry.Exception + string.Join(' ', entry.State.Values);
            text.ShouldNotContain("X-Amz-Signature");
            text.ShouldNotContain(url);
        }

        var audit = await TextAsync(db, "SELECT string_agg(details::text || coalesce(reason_code, '') || resource_type, ' ') FROM socalytics.security_audit_event", ct);
        audit.ShouldNotBeNull();
        audit.ShouldNotContain("X-Amz-Signature");
        audit.ShouldNotContain("http");
        (await TextAsync(db, "SELECT actor_kind || ':' || reason_code FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-grant-refused'", ct))
            .ShouldBe("analyst-manager:foreign-attempt");
    }
}
