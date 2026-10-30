using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class PayloadConformanceTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs, NatsContainerFixture nats)
{
    private const string Digest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static DirectoryInfo RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")))
        {
            dir = dir.Parent;
        }

        return dir;
    }

    private Dictionary<string, string?> WorkerSettings()
    {
        var path = Path.Combine(RepositoryRoot().FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json");
        var settings = new ConfigurationBuilder().AddJsonFile(path).Build().GetSection("Analysis")
            .AsEnumerable(true).ToDictionary(p => "Analysis:" + p.Key, p => p.Value);
        settings["ConnectionStrings:nats"] = nats.ConnectionString;
        settings["Messaging:Nats:ProvisionTopology"] = "true";
        settings["Analysis:Workers:Enabled"] = "true";
        return settings;
    }

    private async Task<FinalizationSetup> StartAsync(CancellationToken ct)
    {
        var setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, configureServices: services =>
            services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
                o.Schemes.Single(x => x.Name == SocAlytics.Platform.Api.Registry.AnalystManagerDPoPDefaults.AuthenticationScheme).HandlerType =
                    typeof(TestManagerAuthenticationHandler)), extraSettings: WorkerSettings());
        await RecordingFinalizationHelper.FinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, 1, ct);
        return setup;
    }

    // The outbox ids of the run's message set, taken once after the publication state of the run is complete.
    private static async Task<HashSet<string>> RunMessageIdsAsync(FinalizationSetup s, CancellationToken ct)
    {
        var ids = new HashSet<string>();
        await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using (var runCmd = new NpgsqlCommand("SELECT id FROM socalytics.analysis_runs LIMIT 1", c))
        {
            if (await runCmd.ExecuteScalarAsync(ct) is not Guid run)
            {
                throw new InvalidOperationException("The run does not exist.");
            }

            await using var cmd = new NpgsqlCommand($"SELECT id FROM socalytics.outbox_messages WHERE {MessageSetFilter}", c);
            cmd.Parameters.AddWithValue("run", run);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetGuid(0).ToString());
            }
        }

        return ids;
    }

    private async Task<List<(string Subject, JsonElement Payload)>> ReadMessagesAsync(FinalizationSetup s, CancellationToken ct)
    {
        var own = await RunMessageIdsAsync(s, ct);
        var found = new HashSet<string>();
        var all = new List<(string, JsonElement)>();
        foreach (var name in new[] { "SOCALYTICS_ANALYSIS_JOBS", "SOCALYTICS_EVENTS" })
        {
            all.AddRange(await ReadStreamAsync(name, own, ct, found));
        }

        var missing = own.Except(found).Order().ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Outbox ids of the run's message set found in neither stream SOCALYTICS_ANALYSIS_JOBS nor SOCALYTICS_EVENTS: " + string.Join(", ", missing));
        }

        return all;
    }

    // A message belongs to the set when its Nats-Msg-Id is an outbox id, optionally followed by ".r<round>" (published more than once).
    private static string? OwnOutboxId(string msgId, HashSet<string> own)
    {
        if (own.Contains(msgId))
        {
            return msgId;
        }

        var cut = msgId.LastIndexOf(".r", StringComparison.Ordinal);
        if (cut > 0 && msgId.Length > cut + 2 && msgId[(cut + 2)..].All(char.IsAsciiDigit) && own.Contains(msgId[..cut]))
        {
            return msgId[..cut];
        }

        return null;
    }

    private async Task<List<(string Subject, JsonElement Payload)>> ReadStreamAsync(string name, HashSet<string> own, CancellationToken ct, HashSet<string> found)
    {
        await using var connection = new NatsConnection(NatsOpts.Default with { Url = nats.ConnectionString });
        var stream = await new NatsJSContext(connection).GetStreamAsync(name, cancellationToken: ct);
        var result = new List<(string, JsonElement)>();
        var info = stream.Info.State;
        for (var seq = info.FirstSeq; seq <= info.LastSeq && info.Messages > 0; seq++)
        {
            try
            {
                var raw = await stream.GetAsync(new StreamMsgGetRequest { Seq = seq }, ct);
                var text = string.IsNullOrEmpty(raw.Message.Hdrs) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(raw.Message.Hdrs));
                var line = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("Nats-Msg-Id:", StringComparison.OrdinalIgnoreCase));
                var outboxId = line is null ? null : OwnOutboxId(line["Nats-Msg-Id:".Length..].Trim(), own);
                if (outboxId is null)
                {
                    continue;
                }

                found.Add(outboxId);
                result.Add((raw.Message.Subject, JsonDocument.Parse(raw.Message.Data.ToArray()).RootElement.Clone()));
            }
            catch (NatsJSApiException)
            {
            }
        }

        return result;
    }

    // Hang guard only: it bounds a stuck run and is never the success condition of a wait.
    private const int HangGuardMilliseconds = 180_000;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMilliseconds(HangGuardMilliseconds - 30_000);

    private const string MessageSetFilter =
        "run_id = @run AND message_type IN ('analysis.ready-work', 'analysis.run-state-changed')";

    private static async Task<long> CountAsync(NpgsqlConnection c, string sql, Guid run, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("run", run);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<List<string>> OutboxRowsAsync(NpgsqlConnection c, string states, Guid run, CancellationToken ct)
    {
        var rows = new List<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, message_type, subject, state, attempt_count, failure_category, next_attempt_at " +
            $"FROM socalytics.outbox_messages WHERE {MessageSetFilter} AND state IN ({states}) ORDER BY created_at, id", c);
        cmd.Parameters.AddWithValue("run", run);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add($"id={reader.GetGuid(0)} message_type={reader.GetString(1)} subject={reader.GetString(2)} state={reader.GetString(3)} " +
                $"attempt_count={reader.GetInt32(4)} failure_category={(reader.IsDBNull(5) ? "-" : reader.GetString(5))} " +
                $"next_attempt_at={(reader.IsDBNull(6) ? "-" : reader.GetDateTime(6).ToString("O"))}");
        }

        return rows;
    }

    // The publication state of the run as the database shows it: true when the run's expected ready-work messages
    // are complete and published. Throws when a message of the run's set is failed or discarded.
    private static async Task<bool> PublicationCompleteAsync(FinalizationSetup s, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
        await c.OpenAsync(ct);

        Guid run;
        await using (var cmd = new NpgsqlCommand("SELECT id FROM socalytics.analysis_runs LIMIT 1", c))
        {
            if (await cmd.ExecuteScalarAsync(ct) is not Guid id)
            {
                return false;
            }

            run = id;
        }

        var broken = await OutboxRowsAsync(c, "'failed', 'discarded'", run, ct);
        if (broken.Count > 0)
        {
            throw new InvalidOperationException("Outbox rows of the run's message set failed or were discarded: " + string.Join("; ", broken));
        }

        if (await CountAsync(c, "SELECT count(*) FROM socalytics.analysis_workflow_nodes WHERE run_id = @run", run, ct) == 0 ||
            await CountAsync(c, "SELECT count(*) FROM socalytics.outbox_messages WHERE " + MessageSetFilter + " AND state = 'pending'", run, ct) > 0)
        {
            return false;
        }

        // Every node of the run has its logical job.
        if (await CountAsync(c,
                "SELECT count(*) FROM socalytics.analysis_workflow_nodes n WHERE n.run_id = @run AND NOT EXISTS " +
                "(SELECT 1 FROM socalytics.analysis_logical_jobs j WHERE j.run_id = n.run_id AND j.node_id = n.id)", run, ct) > 0)
        {
            return false;
        }

        const string readyAndPublished =
            "j.state IN ('Ready', 'Claimed', 'Completed') AND EXISTS (SELECT 1 FROM socalytics.analysis_readiness_occurrences o " +
            "WHERE o.logical_job_id = j.id) AND EXISTS (SELECT 1 FROM socalytics.outbox_messages m WHERE m.run_id = j.run_id " +
            "AND m.logical_job_id = j.id AND m.message_type = 'analysis.ready-work' AND m.state = 'published')";

        // Settled: Ready with its published ready-work row, or waiting on the accepted result of an upstream node of the run.
        // A job waiting on segment materialization or not yet evaluated by the readiness worker has no such edge.
        var unsettled = await CountAsync(c,
            "SELECT count(*) FROM socalytics.analysis_logical_jobs j WHERE j.run_id = @run AND NOT ((" + readyAndPublished + ") OR " +
            "(j.state = 'Waiting' AND EXISTS (SELECT 1 FROM socalytics.analysis_dependency_edges e WHERE e.run_id = j.run_id " +
            "AND e.dependent_node_id = j.node_id)))", run, ct);
        if (unsettled > 0)
        {
            return false;
        }

        return await CountAsync(c,
            "SELECT count(*) FROM socalytics.analysis_logical_jobs j WHERE j.run_id = @run AND j.execution_scope = 'segment' AND " + readyAndPublished,
            run, ct) > 0;
    }

    private static async Task<string> PendingRowsAsync(FinalizationSetup s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
            await c.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("SELECT id FROM socalytics.analysis_runs LIMIT 1", c);
            if (await cmd.ExecuteScalarAsync(cts.Token) is not Guid run)
            {
                return "the run does not exist";
            }

            var rows = await OutboxRowsAsync(c, "'pending'", run, cts.Token);
            return rows.Count == 0 ? "no pending row in the run's message set" : string.Join("; ", rows);
        }
        catch (Exception e) when (e is OperationCanceledException or NpgsqlException)
        {
            return "the pending rows could not be read: " + e.Message;
        }
    }

    private async Task<List<(string Subject, JsonElement Payload)>> WaitForReadyWorkAsync(FinalizationSetup s, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(WaitLimit);
        try
        {
            while (!await PublicationCompleteAsync(s, linked.Token))
            {
                // Drives the publisher on the single fake clock; the delay only paces the condition check.
                s.Factory.Time.Advance(TimeSpan.FromSeconds(3));
                await Task.Delay(250, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            throw new TimeoutException("The run's publication state was not complete before the hang guard. Pending rows: " + await PendingRowsAsync(s));
        }

        return await ReadMessagesAsync(s, ct);
    }

    private static string SchemaFor(string subject) => subject switch
    {
        "analysis.events.run-state-changed" => ContractSchemaIds.RunStateChangedV1,
        _ when subject.StartsWith("analysis.jobs.ready.") => ContractSchemaIds.AnalystJobV1,
        _ => throw new InvalidOperationException(subject),
    };

    private static JsonObject ValidBody(JsonElement job, Guid attempt, long token, int number, string key) => new()
    {
        ["contractVersion"] = "1.0.0",
        ["idempotencyKey"] = key,
        ["teamId"] = job.GetProperty("teamId").GetGuid(),
        ["matchId"] = job.GetProperty("matchId").GetGuid(),
        ["runId"] = job.GetProperty("runId").GetGuid(),
        ["nodeId"] = job.GetProperty("nodeId").GetGuid(),
        ["logicalJobId"] = job.GetProperty("logicalJobId").GetGuid(),
        ["attemptId"] = attempt,
        ["attemptNumber"] = number,
        ["fencingToken"] = token,
        ["outcome"] = "succeeded",
        ["result"] = new JsonObject { ["manifestReference"] = "results/m1.json", ["manifestChecksum"] = Digest },
        ["capability"] = JsonNode.Parse(job.GetProperty("capability").GetRawText()),
        ["schemaVersion"] = JsonNode.Parse(job.GetProperty("schemaVersion").GetRawText()),
        ["executedRuntime"] = new JsonObject
        {
            ["runtimeProfile"] = "cpu", ["runtimeVersion"] = "1.0", ["operatingSystem"] = "linux",
            ["architecture"] = "x64", ["accelerators"] = new JsonArray(),
        },
        ["executedImageDigest"] = JsonNode.Parse(job.GetProperty("pinned").GetProperty("imageDigest").GetRawText()),
        ["executedModelDigest"] = JsonNode.Parse(job.GetProperty("pinned").GetProperty("modelDigest").GetRawText()),
        ["acceptedUpstreamResultIds"] = new JsonArray(),
        ["completedAt"] = "2026-01-01T00:00:00Z",
        ["correlation"] = new JsonObject { ["correlationId"] = Guid.NewGuid() },
    };

    private async Task<HttpResponseMessage> CompleteAsync(FinalizationSetup s, TestManagerClient m, Guid attempt, JsonNode body, CancellationToken ct)
    {
        if (body["result"]?["manifestReference"]?.GetValue<string>() == "results/m1.json")
        {
            await ResultObjectsHelper.ApplyAsync(s.Db, rustFs, attempt, body.AsObject(), ct: ct);
        }

        using var client = s.Factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/completion")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, m.ManagerId.ToString());
        return await client.SendAsync(request, ct);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var messages = await WaitForReadyWorkAsync(s, ct);
        var validator = s.Factory.Services.GetRequiredService<IContractValidator>();

        foreach (var (subject, payload) in messages.Where(m => m.Subject.StartsWith("analysis.jobs.ready.") || m.Subject == "analysis.events.run-state-changed"))
        {
            var result = validator.Validate(SchemaFor(subject), payload);
            result.IsValid.ShouldBeTrue($"{subject}: {string.Join(", ", result.Errors.Select(e => e.Field + ":" + e.Keyword))}");
        }

        var jobs = messages.Where(m => m.Subject.StartsWith("analysis.jobs.ready.")).Select(m => m.Payload).ToList();
        jobs.ShouldContain(j => j.GetProperty("executionScope").GetString() == "segment");
        foreach (var job in jobs)
        {
            if (job.GetProperty("executionScope").GetString() == "segment")
            {
                var segment = job.GetProperty("segment");
                segment.GetProperty("recordingSetVersionId").GetGuid().ShouldBe(job.GetProperty("recordingSetVersionId").GetGuid());
                foreach (var name in new[] { "recordingVersionId", "timelineMappingDigest", "segmentationPolicyDigest", "segmentNumber" })
                {
                    segment.TryGetProperty(name, out _).ShouldBeTrue(name);
                }
            }
            else
            {
                job.TryGetProperty("segment", out _).ShouldBeFalse();
            }
        }
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task CompletionBodiesAreValidatedAndStaleOnesAreRejectedByAcceptance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var job = (await WaitForReadyWorkAsync(s, ct)).First(m => m.Subject.StartsWith("analysis.jobs.ready.")).Payload;
        var validator = s.Factory.Services.GetRequiredService<IContractValidator>();
        using var manager = new TestManagerClient(s.Factory);

        using var claim = await manager.ClaimAsync(
            job.GetProperty("logicalJobId").GetGuid(), job.GetProperty("readinessOccurrenceId").GetGuid(), "claim-1", ct: ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync(ct));
        var claimed = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var attempt = claimed.GetProperty("attemptId").GetGuid();
        var token = claimed.GetProperty("fencingToken").GetInt64();
        var number = claimed.GetProperty("attemptNumber").GetInt32();

        var examples = Path.Combine(RepositoryRoot().FullName, "contracts", "analysis", "attempt-completion", "v1", "examples", "invalid");
        var files = Directory.GetFiles(examples, "*.json").Where(f => Path.GetFileName(f) != "expectations.json").ToList();
        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            var body = JsonNode.Parse(await File.ReadAllTextAsync(file, ct))!.AsObject();
            foreach (var (name, value) in new (string, JsonNode)[]
            {
                ("teamId", job.GetProperty("teamId").GetGuid()), ("matchId", job.GetProperty("matchId").GetGuid()),
                ("runId", job.GetProperty("runId").GetGuid()), ("nodeId", job.GetProperty("nodeId").GetGuid()),
                ("logicalJobId", job.GetProperty("logicalJobId").GetGuid()), ("attemptId", attempt),
                ("attemptNumber", number), ("fencingToken", token),
            })
            {
                if (body.ContainsKey(name))
                {
                    body[name] = value;
                }
            }

            using var response = await CompleteAsync(s, manager, attempt, body, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, Path.GetFileName(file));
        }

        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe(0);
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Active'", ct)).ShouldBe(1);

        var stale = ValidBody(job, attempt, token + 100, number, "stale-1");
        var staleResult = validator.Validate(ContractSchemaIds.AttemptCompletionV1, JsonSerializer.SerializeToElement(stale));
        staleResult.IsValid.ShouldBeTrue(string.Join(", ", staleResult.Errors.Select(e => e.Field + ":" + e.Keyword)));
        using var rejected = await CompleteAsync(s, manager, attempt, stale, ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await rejected.Content.ReadAsStringAsync(ct)).ShouldContain("attempt-obsolete");

        var good = ValidBody(job, attempt, token, number, "good-1");
        validator.Validate(ContractSchemaIds.AttemptCompletionV1, JsonSerializer.SerializeToElement(good)).IsValid.ShouldBeTrue();
        using var accepted = await CompleteAsync(s, manager, attempt, good, ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(ct));
    }
}
