using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class HostLabelTests
{
    private static JsonNode PairingMetadata(ManagerTestHost host)
    {
        var request = host.Platform.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/pairings", StringComparison.Ordinal));
        return JsonNode.Parse(request.Body)!["deviceMetadata"]!;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0007label")]
    [InlineData("line\nbreak")]
    public async Task CoordinatorRefusesInvalidLabelsBeforeAnyRequest(string label)
    {
        await using var host = new ManagerTestHost();
        var result = await host.Control.RegisterAsync(
            new RegisterRequest("https://platform.test", null, label), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.Code.ShouldBe("invalid-request");
        host.Platform.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CoordinatorRefusesLabelsLongerThan64Characters()
    {
        await using var host = new ManagerTestHost();
        var result = await host.Control.RegisterAsync(
            new RegisterRequest("https://platform.test", null, new string('a', 65)), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        host.Platform.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task PairingRequestCarriesExactlyTheEnteredLabel()
    {
        await using var host = new ManagerTestHost();
        var label = new string('x', 64);
        (await host.Control.RegisterAsync(
            new RegisterRequest("https://platform.test", null, $"  {label} "), TestContext.Current.CancellationToken))
            .Succeeded.ShouldBeTrue();

        ((string)PairingMetadata(host)["hostLabel"]!).ShouldBe(label);
    }

    [Fact]
    public async Task PairingRequestCarriesNoLabelWhenNoneEntered()
    {
        await using var host = new ManagerTestHost();
        (await host.Control.RegisterAsync(new RegisterRequest("https://platform.test"), TestContext.Current.CancellationToken))
            .Succeeded.ShouldBeTrue();

        PairingMetadata(host)["hostLabel"].ShouldBeNull();
    }

    [Fact]
    public async Task MachineNameNeverAppearsInAnyRequest()
    {
        await using var host = new ManagerTestHost();
        (await host.Control.RegisterAsync(
            new RegisterRequest("https://platform.test", null, "build-agent"), TestContext.Current.CancellationToken))
            .Succeeded.ShouldBeTrue();

        host.Platform.Requests.ShouldNotBeEmpty();
        host.Platform.Requests.ShouldAllBe(r =>
            !r.Body.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !r.Uri.ToString().Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0007label")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task CliRefusesInvalidLabelsLocallyWithAMessage(string label)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await ManagerCli.RunAsync(
            ["register", "--platform", "https://platform.example", "--host-label", label],
            Path.Combine(Path.GetTempPath(), "sa-no-such-" + Guid.NewGuid().ToString("N"), "manager.sock"), output, error);

        code.ShouldBe(ManagerCli.Usage);
        error.ToString().ShouldContain("host label");
        output.ToString().ShouldBeEmpty();
    }
}
