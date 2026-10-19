using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Compose.Tests.Support;
using SocAlytics.Platform.Images.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Compose.Tests;

public sealed partial class StampLifecycleTests
{
    private const string OverrideFile = "src/platform/Tests/SocAlytics.Platform.Compose.Tests/compose.evidence.yaml";
    private const string Bucket = "socalytics-recordings";
    private const string WorkflowCheck = "analysis-workflow";

    private static readonly TimeSpan HealthyWithin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AliveWithin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RecoverWithin = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Stamp_starts_in_order_reaches_healthy_with_a_default_workflow_survives_restarts_and_tears_down_cleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var stamp = new StampFixture(OverrideFile);
        await stamp.InitializeAsync();
        try
        {
            var upStarted = DateTime.UtcNow;
            await stamp.UpAsync();

            foreach (var service in new[] { "postgres", "nats", "storage", "web" })
            {
                var id = await ContainerIdAsync(stamp, service, ct);
                await WaitUntilAsync($"{service} to be healthy", HealthyWithin, async () => await DockerAsync(["inspect", "--format", "{{.State.Health.Status}}", id], ct) == "healthy", ct);
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var shell = await http.GetAsync(stamp.WebOrigin + "/", ct);
            shell.IsSuccessStatusCode.ShouldBeTrue();
            (await shell.Content.ReadAsStringAsync(ct)).ShouldContain("<html", Case.Insensitive);

            // The Migrator finished before the API container started.
            var migratorId = await ContainerIdAsync(stamp, "migrator", ct);
            await WaitUntilAsync("the migrator to exit", AliveWithin, async () => await DockerAsync(["inspect", "--format", "{{.State.Status}}", migratorId], ct) == "exited", ct);
            (await DockerAsync(["inspect", "--format", "{{.State.ExitCode}}", migratorId], ct)).ShouldBe("0");
            var apiId = await ContainerIdAsync(stamp, "api", ct);
            var migratorFinished = ParseDockerTime(await DockerAsync(["inspect", "--format", "{{.State.FinishedAt}}", migratorId], ct));
            var apiStarted = ParseDockerTime(await DockerAsync(["inspect", "--format", "{{.State.StartedAt}}", apiId], ct));
            migratorFinished.ShouldBeLessThan(apiStarted);

            try
            {
                await WaitUntilAsync("/alive through the web container", AliveWithin - (DateTime.UtcNow - upStarted), async () => (await ProbeAsync(stamp, "/alive", ct)).Ok, ct);
            }
            catch (TimeoutException exception)
            {
                var probe = await ProbeAsync(stamp, "/alive", ct);
                var logs = (await stamp.Cli.RunAsync("logs", "--no-color", "--tail", "60", "api", "web")).Output;
                throw new TimeoutException($"{exception.Message}\nLast probe: {probe.Output}\n{logs}");
            }

            // The default workflow is not arranged yet: only its check may fail.
            await WaitUntilAsync("/health to fail on the default workflow only", AliveWithin, async () =>
            {
                var since = DateTime.UtcNow.AddSeconds(-2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                if ((await ProbeAsync(stamp, "/health", ct)).Ok)
                {
                    return false;
                }

                var logs = (await stamp.Cli.RunAsync("logs", "--no-color", "--since", since, "api")).Output;
                var failing = UnhealthyCheck().Matches(logs).Select(match => match.Groups[1].Value).Distinct().ToList();
                return failing.Count == 1 && failing[0] == WorkflowCheck;
            }, ct);

            var port = await PublishedPostgresPortAsync(stamp);
            var connectionString = ApplicationConnectionString(stamp, port);
            await DefaultWorkflowArrangement.SeedAsync(connectionString, ct);
            await WaitUntilAsync("/health to become healthy without a restart", RecoverWithin, async () => (await ProbeAsync(stamp, "/health", ct)).Ok, ct);
            ParseDockerTime(await DockerAsync(["inspect", "--format", "{{.State.StartedAt}}", apiId], ct)).ShouldBe(apiStarted);

            var storage = await stamp.Cli.RunAsync(
                "exec", "-T", "storage", "curl", "-fsS", "-I", "--aws-sigv4", "aws:amz:us-east-1:s3",
                "--user", $"{stamp.Get("SOCALYTICS_STORAGE_ACCESS_KEY")}:{stamp.Get("SOCALYTICS_STORAGE_SECRET_KEY")}",
                $"http://127.0.0.1:9000/{Bucket}");
            storage.ExitCode.ShouldBe(0, storage.Output);

            var streamsBefore = await StreamNamesAsync(stamp);
            streamsBefore.ShouldNotBeEmpty();
            (await stamp.Cli.RunAsync("restart", "nats")).ExitCode.ShouldBe(0);
            await WaitUntilAsync("/health to recover after the NATS restart", RecoverWithin, async () => (await ProbeAsync(stamp, "/health", ct)).Ok, ct);
            await WaitUntilAsync("the JetStream streams to be kept", RecoverWithin, async () => (await StreamNamesAsync(stamp)).SequenceEqual(streamsBefore), ct);

            var clubsBefore = await CountAsync(connectionString, "SELECT count(*) FROM socalytics.club", ct);
            var journalBefore = await CountAsync(connectionString, "SELECT count(*) FROM socalytics_migrations.history", ct);
            clubsBefore.ShouldBe(1);
            journalBefore.ShouldBeGreaterThan(0);
            var finishedBefore = await DockerAsync(["inspect", "--format", "{{.State.FinishedAt}}", migratorId], ct);

            (await stamp.Cli.RunAsync("stop")).ExitCode.ShouldBe(0);
            (await stamp.Cli.RunAsync("start")).ExitCode.ShouldBe(0);
            port = await PublishedPostgresPortAsync(stamp);
            connectionString = ApplicationConnectionString(stamp, port);

            await WaitUntilAsync("the migrator to run and exit again", HealthyWithin, async () =>
                await DockerAsync(["inspect", "--format", "{{.State.Status}}", migratorId], ct) == "exited"
                && await DockerAsync(["inspect", "--format", "{{.State.FinishedAt}}", migratorId], ct) != finishedBefore, ct);
            (await DockerAsync(["inspect", "--format", "{{.State.ExitCode}}", migratorId], ct)).ShouldBe("0");
            await WaitUntilAsync("/health to be healthy after start", HealthyWithin, async () => (await ProbeAsync(stamp, "/health", ct)).Ok, ct);
            (await CountAsync(connectionString, "SELECT count(*) FROM socalytics.club", ct)).ShouldBe(clubsBefore);
            (await CountAsync(connectionString, "SELECT count(*) FROM socalytics_migrations.history", ct)).ShouldBe(journalBefore);
        }
        finally
        {
            await stamp.DisposeAsync();
        }

        await AssertNothingRemainsAsync(stamp.ProjectName, ct);
    }

    [Fact]
    public async Task Stamp_with_a_wrong_migrator_password_ends_with_a_failed_migrator_and_no_api_container()
    {
        var ct = TestContext.Current.CancellationToken;
        var stamp = new StampFixture(OverrideFile);
        await stamp.InitializeAsync();
        try
        {
            // The database role keeps the password it was created with, so changing the value afterwards makes the Migrator's wrong.
            var first = await stamp.Cli.RunAsync("up", "-d", "--wait", "postgres");
            first.ExitCode.ShouldBe(0, first.Output);
            stamp.Set("SOCALYTICS_MIGRATOR_DB_PASSWORD", "wrong-" + Guid.NewGuid().ToString("N"));

            var up = await stamp.Cli.RunAsync("up", "-d");
            up.ExitCode.ShouldNotBe(0, up.Output);

            var migratorId = await ContainerIdAsync(stamp, "migrator", ct);
            await WaitUntilAsync("the migrator to exit", RecoverWithin, async () => await DockerAsync(["inspect", "--format", "{{.State.Status}}", migratorId], ct) == "exited", ct);
            (await DockerAsync(["inspect", "--format", "{{.State.ExitCode}}", migratorId], ct)).ShouldNotBe("0");
            // Compose may create the api container, but it must never have been started.
            var apiIds = (await stamp.Cli.RunAsync("ps", "-a", "-q", "api")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var id in apiIds)
            {
                (await DockerAsync(["inspect", "--format", "{{.State.Status}}", id], ct)).ShouldBe("created");
            }
        }
        finally
        {
            await stamp.DisposeAsync();
        }

        await AssertNothingRemainsAsync(stamp.ProjectName, ct);
    }

    private static async Task<string> ContainerIdAsync(StampFixture stamp, string service, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (exitCode, output) = await stamp.Cli.RunAsync("ps", "-a", "-q", service);
        exitCode.ShouldBe(0, output);
        var id = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        id.ShouldNotBeNullOrWhiteSpace($"The {service} container must exist.");
        return id;
    }

    private static async Task<(bool Ok, string Output)> ProbeAsync(StampFixture stamp, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (exitCode, output) = await stamp.Cli.RunAsync("exec", "-T", "web", "wget", "-qO-", "http://api:8080" + path);
        return (exitCode == 0 && output.Trim() == "Healthy", output);
    }

    private static async Task<int> PublishedPostgresPortAsync(StampFixture stamp)
    {
        var (exitCode, output) = await stamp.Cli.RunAsync("port", "postgres", "5432");
        exitCode.ShouldBe(0, output);
        return int.Parse(output.Trim().Split('\n')[^1].Trim().Split(':')[^1], CultureInfo.InvariantCulture);
    }

    private static string ApplicationConnectionString(StampFixture stamp, int port) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "socalytics_app",
            Password = stamp.Get("SOCALYTICS_APP_DB_PASSWORD"),
            Database = "socalytics",
            Pooling = false,
        }.ConnectionString;

    private static async Task<long> CountAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> StreamNamesAsync(StampFixture stamp)
    {
        var (exitCode, output) = await stamp.Cli.RunAsync("exec", "-T", "nats", "wget", "-qO-", "http://127.0.0.1:8222/jsz?accounts=true&streams=true");
        if (exitCode != 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("account_details", out var accounts))
            {
                return [];
            }

            return accounts.EnumerateArray()
                .Where(account => account.TryGetProperty("stream_detail", out _))
                .SelectMany(account => account.GetProperty("stream_detail").EnumerateArray())
                .Select(stream => stream.GetProperty("name").GetString()!)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task AssertNothingRemainsAsync(string project, CancellationToken ct)
    {
        var filter = $"label=com.docker.compose.project={project}";
        (await DockerAsync(["ps", "-a", "-q", "--filter", filter], ct)).ShouldBeEmpty();
        (await DockerAsync(["network", "ls", "-q", "--filter", filter], ct)).ShouldBeEmpty();
        (await DockerAsync(["volume", "ls", "-q", "--filter", filter], ct)).ShouldBeEmpty();
    }

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("The docker CLI could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        process.ExitCode.ShouldBe(0, await error);
        return (await output).Trim();
    }

    // Docker reports nanosecond precision, which DateTimeOffset cannot parse.
    private static DateTimeOffset ParseDockerTime(string value) =>
        DateTimeOffset.Parse(FractionDigits().Replace(value, match => match.Value[..Math.Min(match.Value.Length, 8)]), CultureInfo.InvariantCulture);

    private static async Task WaitUntilAsync(string description, TimeSpan within, Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        do
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Timed out after {within} waiting for {description}.");
    }

    [GeneratedRegex(@"Health check (\S+) with status Unhealthy")]
    private static partial Regex UnhealthyCheck();

    [GeneratedRegex(@"\.\d+")]
    private static partial Regex FractionDigits();
}
