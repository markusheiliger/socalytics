using System.Net;
using Shouldly;
using SocAlytics.Platform.Images.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Images.Tests;

[Collection(ImageBuildCollection.Name)]
public sealed class ApiImageTests(StampDependenciesFixture stamp) : IClassFixture<StampDependenciesFixture>
{
    private const string Dockerfile = "src/platform/SocAlytics.Platform.Api/Dockerfile";
    private const int Port = 8080;

    private static readonly TimeSpan AliveWithin = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthyWithin = TimeSpan.FromSeconds(60);

    private ContainerProbe Probe => stamp.Probe;

    [Fact]
    public async Task Api_runs_read_only_as_the_unprivileged_user_and_reports_alive_and_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await stamp.Images.BuildAsync(Dockerfile);

        var id = await StartAsync(image, stamp.ApiEnvironment(), ct: ct);
        var address = await AddressAsync(id, ct);

        (await WaitForStatusAsync(address, "/alive", HttpStatusCode.OK, AliveWithin, id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));
        (await WaitForStatusAsync(address, "/health", HttpStatusCode.OK, HealthyWithin, id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));

        var uids = await Probe.ProcessUidsAsync(id, ct);
        uids.ShouldNotBeEmpty();
        uids.ShouldAllBe(uid => uid == "1654");
        (await Probe.InspectContainerAsync(id, ct)).GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Api_with_an_arbitrary_non_root_user_still_answers_alive()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await stamp.Images.BuildAsync(Dockerfile);

        var id = await StartAsync(image, stamp.ApiEnvironment(), "12345:0", ct);
        var address = await AddressAsync(id, ct);

        (await WaitForStatusAsync(address, "/alive", HttpStatusCode.OK, AliveWithin, id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));
    }

    [Fact]
    public async Task Api_with_an_unreachable_database_is_alive_but_not_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await stamp.Images.BuildAsync(Dockerfile);

        var id = await StartAsync(image, stamp.ApiEnvironment("unreachable.invalid"), ct: ct);
        var address = await AddressAsync(id, ct);

        (await WaitForStatusAsync(address, "/alive", HttpStatusCode.OK, AliveWithin, id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));
        (await WaitForStatusAsync(address, "/health", HttpStatusCode.ServiceUnavailable, HealthyWithin, id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));
        (await Probe.IsRunningAsync(id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));
    }

    [Fact]
    public async Task Api_without_a_required_option_exits_with_the_options_validation_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await stamp.Images.BuildAsync(Dockerfile);
        var environment = stamp.ApiEnvironment();
        environment.Remove("Stamp__Id").ShouldBeTrue();

        var run = await Probe.RunToCompletionAsync(image, stamp.NetworkName, environment, ct: ct);

        run.ExitCode.ShouldNotBe(0, run.Logs);
        run.Logs.ShouldContain("Stamp:Id");
    }

    [Fact]
    public async Task Api_image_holds_no_migration_path_and_no_development_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await stamp.Images.BuildAsync(Dockerfile);

        var files = await Probe.ListImageFilesAsync(image, ct);

        files.ShouldContain("/app/SocAlytics.Platform.Api.dll");
        files.ShouldNotContain("/app/SocAlytics.Platform.Migrator.dll");
        files.ShouldNotContain(file => Path.GetFileName(file).StartsWith("dbup", StringComparison.OrdinalIgnoreCase)
            && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        files.ShouldNotContain("/app/appsettings.Development.json");
        files.ShouldNotContain(file => Path.GetFileName(file) == "appsettings.Development.json");
    }

    private async Task<string> StartAsync(string image, IReadOnlyDictionary<string, string> environment, string? user = null, CancellationToken ct = default)
    {
        var id = await Probe.CreateAsync(image, stamp.NetworkName, environment, user, ct: ct);
        await Probe.StartAsync(id, ct);
        return id;
    }

    private async Task<string> AddressAsync(string id, CancellationToken ct)
    {
        var inspection = await Probe.InspectContainerAsync(id, ct);
        var ip = inspection.GetProperty("NetworkSettings").GetProperty("Networks").GetProperty(stamp.NetworkName).GetProperty("IPAddress").GetString();
        ip.ShouldNotBeNullOrWhiteSpace();
        return $"http://{ip}:{Port}";
    }

    private async Task<bool> WaitForStatusAsync(string address, string path, HttpStatusCode expected, TimeSpan within, string containerId, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(address + path, ct);
                if (response.StatusCode == expected)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            if (!await Probe.IsRunningAsync(containerId, ct))
            {
                return false;
            }

            await Task.Delay(500, ct);
        }

        return false;
    }

    private async Task<string> DiagnosticsAsync(string id, CancellationToken ct) =>
        $"Container {id} logs:\n{await Probe.LogsAsync(id, ct)}";
}
