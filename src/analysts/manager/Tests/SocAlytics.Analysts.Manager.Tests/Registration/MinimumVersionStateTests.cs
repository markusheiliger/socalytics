using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class MinimumVersionStateTests
{
    private const string BuildVersion = "1.0.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Adds the configured minimum to status responses, and to registration reads only when asked.</summary>
    private sealed class MinimumInjector : DelegatingHandler
    {
        public string? Minimum { get; set; }

        public bool IncludeInRegistrationReads { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            var isStatus = path.EndsWith("/self/status", StringComparison.Ordinal);
            var isRead = path.EndsWith("/self/registration", StringComparison.Ordinal) && IncludeInRegistrationReads;
            if ((isStatus || isRead) && response.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                node["minimumManagerVersion"] = Minimum;
                response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"));
            }

            return response;
        }
    }

    private static ManagerTestHost NewHost(MinimumInjector injector) =>
        new(configure: services =>
        {
            services.RemoveAll<ManagerBuildVersion>();
            services.AddSingleton(new ManagerBuildVersion(BuildVersion));
            services.AddHttpClient<PlatformClient>().AddHttpMessageHandler(() => injector);
        });

    private static async Task<RegistrationStatusMonitor> Activate(ManagerTestHost host)
    {
        var key = await host.Keys.CreateKeyAsync(false, Ct);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(Ct);
        return host.Services.GetRequiredService<RegistrationStatusMonitor>();
    }

    [Theory]
    [InlineData("1.1.0", true)]
    [InlineData("2.0.0", true)]
    [InlineData("1.0.0", false)]
    [InlineData("0.9.9", false)]
    [InlineData(null, false)]
    [InlineData("1.0", true)]
    [InlineData("v1.0.0", true)]
    public async Task Status_response_minimum_decides_the_state(string? minimum, bool below)
    {
        var injector = new MinimumInjector { Minimum = minimum };
        await using var host = NewHost(injector);
        var monitor = await Activate(host);
        var state = host.Services.GetRequiredService<MinimumVersionState>();
        state.IsBelow.ShouldBeFalse();

        await monitor.CheckOnceAsync(Ct);

        state.IsBelow.ShouldBe(below);
        state.Minimum.ShouldBe(minimum);
        state.BuildVersion.ShouldBe(BuildVersion);
    }

    [Fact]
    public async Task A_lowered_or_removed_minimum_clears_the_state_with_the_next_check()
    {
        var injector = new MinimumInjector { Minimum = "2.0.0" };
        await using var host = NewHost(injector);
        var monitor = await Activate(host);
        var state = host.Services.GetRequiredService<MinimumVersionState>();

        await monitor.CheckOnceAsync(Ct);
        state.IsBelow.ShouldBeTrue();

        injector.Minimum = "1.0.0";
        await monitor.CheckOnceAsync(Ct);
        state.IsBelow.ShouldBeFalse();

        injector.Minimum = "3.0.0";
        await monitor.CheckOnceAsync(Ct);
        state.IsBelow.ShouldBeTrue();

        injector.Minimum = null;
        await monitor.CheckOnceAsync(Ct);
        state.IsBelow.ShouldBeFalse();
        state.Minimum.ShouldBeNull();
    }

    [Fact]
    public void Update_clears_a_platform_refusal()
    {
        var state = new MinimumVersionState(new ManagerBuildVersion(BuildVersion));

        state.MarkRefused("2.0.0");
        state.IsBelow.ShouldBeTrue();
        state.RefusedByPlatform.ShouldBeTrue();

        state.Update("1.0.0");
        state.IsBelow.ShouldBeFalse();
        state.RefusedByPlatform.ShouldBeFalse();
    }

    [Fact]
    public async Task The_last_value_is_kept_across_get_only_checks()
    {
        var injector = new MinimumInjector { Minimum = "2.0.0", IncludeInRegistrationReads = true };
        await using var host = NewHost(injector);
        var monitor = await Activate(host);
        var state = host.Services.GetRequiredService<MinimumVersionState>();
        await monitor.CheckOnceAsync(Ct);
        state.IsBelow.ShouldBeTrue();

        // While unregistering, the monitor reads the registration without sending a status report.
        host.Platform.UnregistrationUnreachableCount = 1;
        (await host.Control.UnregisterAsync()).Succeeded.ShouldBeTrue();
        host.Control.GetStatus().RegistrationState.ShouldBe("unregistering");
        injector.Minimum = null;
        var reports = host.Platform.StatusReports.Count();

        await monitor.CheckOnceAsync(Ct);

        host.Platform.StatusReports.Count().ShouldBe(reports);
        state.Minimum.ShouldBe("2.0.0");
        state.IsBelow.ShouldBeTrue();
    }

    [Fact]
    public async Task A_restart_starts_not_below_until_the_first_check()
    {
        var injector = new MinimumInjector { Minimum = "2.0.0" };
        await using (var first = NewHost(injector))
        {
            var monitor = await Activate(first);
            await monitor.CheckOnceAsync(Ct);
            first.Services.GetRequiredService<MinimumVersionState>().IsBelow.ShouldBeTrue();
        }

        await using var restarted = NewHost(new MinimumInjector { Minimum = "2.0.0" });
        var state = restarted.Services.GetRequiredService<MinimumVersionState>();
        state.IsBelow.ShouldBeFalse();
        state.Minimum.ShouldBeNull();

        var restartedMonitor = await Activate(restarted);
        await restartedMonitor.CheckOnceAsync(Ct);
        state.IsBelow.ShouldBeTrue();
    }
}
