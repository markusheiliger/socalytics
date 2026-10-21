using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Compose.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Compose.Tests;

public sealed class StampIsolationTests
{
    private const string ProjectLabel = "com.docker.compose.project";

    private static readonly TimeSpan ReachableWithin = TimeSpan.FromMinutes(5);

    private static readonly (string Service, int Port)[] ForeignTargets = [("api", 8080), ("postgres", 5432), ("nats", 4222), ("storage", 9000)];

    [Fact]
    public async Task Two_stamps_on_one_host_are_isolated_and_independently_removable()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = new StampFixture();
        var b = new StampFixture();
        var c = new StampFixture();
        try
        {
            await a.InitializeAsync();
            await b.InitializeAsync();
            await c.InitializeAsync();

            a.WebPort.ShouldNotBe(b.WebPort);
            foreach (var secret in new[] { "SOCALYTICS_POSTGRES_PASSWORD", "SOCALYTICS_MIGRATOR_DB_PASSWORD", "SOCALYTICS_APP_DB_PASSWORD", "SOCALYTICS_STORAGE_SECRET_KEY", "SOCALYTICS_NATS_OPERATOR_SEED", "SOCALYTICS_FIRST_CLUB_ADMIN_INITIAL_PASSWORD" })
            {
                a.Get(secret).ShouldNotBeNullOrWhiteSpace(secret);
                a.Get(secret).ShouldNotBe(b.Get(secret), secret);
            }

            await a.UpAsync();
            await b.UpAsync();

            using var originA = new OriginClient(a.WebOrigin);
            using var originB = new OriginClient(b.WebOrigin);
            await WaitForApiAsync(originA, ct);
            await WaitForApiAsync(originB, ct);

            var clubA = await SignInAndReadClubNameAsync(a, originA, ct);
            var clubB = await SignInAndReadClubNameAsync(b, originB, ct);
            clubA.ShouldBe(a.Get("SOCALYTICS_CLUB_DISPLAY_NAME"));
            clubB.ShouldBe(b.Get("SOCALYTICS_CLUB_DISPLAY_NAME"));
            clubA.ShouldNotBe(clubB);

            // Every container, network, and volume belongs to exactly one of the two projects, and no name is shared.
            foreach (var kind in new[] { "container", "network", "volume" })
            {
                var ofA = await ResourcesAsync(kind, a.ProjectName, ct);
                var ofB = await ResourcesAsync(kind, b.ProjectName, ct);
                ofA.ShouldNotBeEmpty(kind);
                ofB.ShouldNotBeEmpty(kind);
                ofA.Select(resource => resource.Id).Intersect(ofB.Select(resource => resource.Id)).ShouldBeEmpty(kind);
                ofA.Select(resource => resource.Name).Intersect(ofB.Select(resource => resource.Name)).ShouldBeEmpty(kind);
                ofA.ShouldAllBe(resource => resource.Project == a.ProjectName);
                ofB.ShouldAllBe(resource => resource.Project == b.ProjectName);
            }

            // Stamp B's web container reaches its own API (the control) but none of stamp A's services.
            (await b.Cli.RunAsync("exec", "-T", "web", "sh", "-c", "nc -w 3 api 8080 </dev/null")).ExitCode.ShouldBe(0, "The control connection to the own API must succeed.");
            var addressesOfA = new List<string>();
            foreach (var (service, port) in ForeignTargets)
            {
                var id = await ContainerIdAsync(a, service);
                var addresses = (await DockerAsync(["inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}", id], ct))
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                addresses.ShouldNotBeEmpty(service);
                foreach (var address in addresses)
                {
                    addressesOfA.Add(address);
                    var reached = await b.Cli.RunAsync("exec", "-T", "web", "sh", "-c", $"nc -w 3 {address} {port} </dev/null");
                    reached.ExitCode.ShouldNotBe(0, $"Stamp B reached stamp A's {service} at {address}:{port}.");
                }
            }

            // Service names resolve to the own stamp's services only.
            var ownApi = await DockerAsync(["inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}", await ContainerIdAsync(b, "api")], ct);
            var resolvedApi = await b.Cli.RunAsync("exec", "-T", "web", "getent", "hosts", "api");
            resolvedApi.ExitCode.ShouldBe(0, resolvedApi.Output);
            ownApi.Split(' ', StringSplitOptions.RemoveEmptyEntries).ShouldContain(resolvedApi.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]);
            foreach (var service in ForeignTargets.Select(target => target.Service))
            {
                var resolved = await b.Cli.RunAsync("exec", "-T", "web", "getent", "hosts", service);
                foreach (var address in addressesOfA)
                {
                    resolved.Output.ShouldNotContain(address, Case.Sensitive, $"The name '{service}' resolved to stamp A.");
                }
            }

            var runningOfB = await RunningContainersAsync(b, ct);
            runningOfB.ShouldNotBeEmpty();

            // Tearing down stamp A with its volumes leaves stamp B running and its club unchanged.
            var down = await a.Cli.RunAsync("down", "--volumes", "--remove-orphans");
            down.ExitCode.ShouldBe(0, down.Output);
            await AssertNothingRemainsAsync(a.ProjectName, ct);
            (await RunningContainersAsync(b, ct)).ShouldBe(runningOfB);
            (await ReadClubNameAsync(originB, ct)).ShouldBe(clubB);

            // A third stamp that publishes stamp B's port fails with a port conflict while stamp B keeps running.
            c.Set("SOCALYTICS_WEB_PORT", b.WebPort.ToString());
            var conflict = await c.Cli.RunAsync("up", "-d");
            conflict.ExitCode.ShouldNotBe(0, conflict.Output);
            conflict.Output.ShouldMatch("(?i)(port is already allocated|address already in use|ports are not available)");
            (await RunningContainersAsync(b, ct)).ShouldBe(runningOfB);
            (await ReadClubNameAsync(originB, ct)).ShouldBe(clubB);
        }
        finally
        {
            await c.DisposeAsync();
            await b.DisposeAsync();
            await a.DisposeAsync();
        }

        foreach (var project in new[] { a.ProjectName, b.ProjectName, c.ProjectName })
        {
            await AssertNothingRemainsAsync(project, ct);
        }
    }

    private static async Task<string> SignInAndReadClubNameAsync(StampFixture stamp, OriginClient origin, CancellationToken ct)
    {
        var accountName = stamp.Get("SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME");
        var initialPassword = stamp.Get("SOCALYTICS_FIRST_CLUB_ADMIN_INITIAL_PASSWORD");
        await origin.SignInAsync(accountName, initialPassword, ct);
        var newPassword = "changed-" + Guid.NewGuid().ToString("N");
        using var changed = await origin.ChangeOwnPasswordAsync(initialPassword, newPassword, ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync(ct));
        await origin.SignInAsync(accountName, newPassword, ct);
        return await ReadClubNameAsync(origin, ct);
    }

    private static async Task<string> ReadClubNameAsync(OriginClient origin, CancellationToken ct)
    {
        using var response = await origin.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("displayName").GetString()!;
    }

    private static async Task WaitForApiAsync(OriginClient origin, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReachableWithin;
        var last = "no response";
        do
        {
            try
            {
                using var response = await origin.SendAsync(HttpMethod.Get, "/alive", null, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                last = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException exception)
            {
                last = exception.Message;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                last = "request timed out";
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Timed out after {ReachableWithin} waiting for /alive through the origin ({last}).");
    }

    private static async Task<string> ContainerIdAsync(StampFixture stamp, string service)
    {
        var (exitCode, output) = await stamp.Cli.RunAsync("ps", "-a", "-q", service);
        exitCode.ShouldBe(0, output);
        var id = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        id.ShouldNotBeNullOrWhiteSpace($"The {service} container must exist.");
        return id;
    }

    private static async Task<IReadOnlyList<string>> RunningContainersAsync(StampFixture stamp, CancellationToken ct) =>
        (await DockerAsync(["ps", "--no-trunc", "--filter", $"label={ProjectLabel}={stamp.ProjectName}", "--filter", "status=running", "--format", "{{.ID}}"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static async Task<IReadOnlyList<(string Id, string Name, string Project)>> ResourcesAsync(string kind, string project, CancellationToken ct)
    {
        var format = kind switch
        {
            "container" => "{{.ID}}|{{.Names}}|{{.Label \"" + ProjectLabel + "\"}}",
            "network" => "{{.ID}}|{{.Name}}|{{.Label \"" + ProjectLabel + "\"}}",
            _ => "{{.Name}}|{{.Name}}|{{.Label \"" + ProjectLabel + "\"}}",
        };
        var arguments = kind == "container"
            ? new[] { "ps", "-a", "--filter", $"label={ProjectLabel}={project}", "--format", format }
            : [kind, "ls", "--filter", $"label={ProjectLabel}={project}", "--format", format];
        var output = await DockerAsync(arguments, ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Select(parts => (parts[0], parts[1], parts[2]))
            .ToList();
    }

    private static async Task AssertNothingRemainsAsync(string project, CancellationToken ct)
    {
        var filter = $"label={ProjectLabel}={project}";
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
}
