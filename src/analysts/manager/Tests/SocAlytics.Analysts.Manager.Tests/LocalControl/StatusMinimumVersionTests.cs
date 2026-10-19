using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Server;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.LocalControl;

public sealed class StatusMinimumVersionTests : IDisposable
{
    private const string Sentence = "Update required: Manager 0.1.0 is below the stamp minimum 0.2.0.";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-mv-" + Guid.NewGuid().ToString("N")[..8]);

    private string SocketPath => Path.Combine(_dir, "run", "manager.sock");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Wraps the shared fake and adds the update notice to its status.</summary>
    private sealed class UpdateControl(FakeManagerControl inner) : IManagerControl
    {
        public UpdateRequiredStatus? Update { get; set; }

        public event EventHandler? StatusChanged { add { } remove { } }

        public event EventHandler? ExitCompleted { add { } remove { } }

        public event EventHandler? ShowStatusRequested { add { } remove { } }

        public void RequestShowStatus() => inner.RequestShowStatus();

        public ManagerStatusSnapshot GetStatus() => inner.GetStatus() with { UpdateRequired = Update };

        public Task<ControlResult> PauseAsync() => inner.PauseAsync();

        public Task<ControlResult> PauseAsync(TimeSpan duration) => inner.PauseAsync(duration);

        public Task<ControlResult> PauseUntilAsync(DateTimeOffset endUtc) => inner.PauseUntilAsync(endUtc);

        public Task<ControlResult> ResumeAsync() => inner.ResumeAsync();

        public Task<ControlResult> ExitAsync() => inner.ExitAsync();

        public Task<ControlResult> SessionEndingAsync() => inner.SessionEndingAsync();

        public SocAlytics.Analysts.Manager.Work.ConcurrencySnapshot GetConcurrency() => inner.GetConcurrency();

        public Task<ControlResult> SetHostLocalConcurrencyAsync(int limit) => inner.SetHostLocalConcurrencyAsync(limit);

        public Task<ControlResult> SetAutostartAsync(bool enabled) => inner.SetAutostartAsync(enabled);

        public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
            inner.RegisterAsync(request, cancellationToken);

        public Task CancelRegistrationAsync() => inner.CancelRegistrationAsync();

        public Task<ControlResult> UnregisterAsync() => inner.UnregisterAsync();

        public Task<ControlResult> DiscardLocalRegistrationAsync() => inner.DiscardLocalRegistrationAsync();
    }

    private static UpdateControl Control(UpdateRequiredStatus? update = null) =>
        new(new FakeManagerControl { State = "active" }) { Update = update };

    private static bool Valid(JsonObject report) => LocalControlSchema.Value.Evaluate(JsonSerializer.SerializeToElement(
        new { protocolVersion = 1, ok = true, result = report })).IsValid;

    [Fact]
    public void Update_object_is_serialized_and_valid_against_the_schema()
    {
        var report = LocalControlServer.StatusReport(Control(new UpdateRequiredStatus(true, "0.1.0", "0.2.0")));
        var update = report["update"]!.AsObject();
        update["required"]!.GetValue<bool>().ShouldBeTrue();
        update["managerVersion"]!.GetValue<string>().ShouldBe("0.1.0");
        update["minimumVersion"]!.GetValue<string>().ShouldBe("0.2.0");
        report.ContainsKey("updateRequired").ShouldBeFalse();
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Unparsable_minimum_is_reported_as_received()
    {
        var report = LocalControlServer.StatusReport(Control(new UpdateRequiredStatus(true, "0.1.0", "0.2")));
        report["update"]!["minimumVersion"]!.GetValue<string>().ShouldBe("0.2");
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Update_object_is_absent_when_not_required()
    {
        var report = LocalControlServer.StatusReport(Control());
        report.ContainsKey("update").ShouldBeFalse();
        report.ContainsKey("updateRequired").ShouldBeFalse();
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Update_object_with_required_false_is_invalid()
    {
        var report = LocalControlServer.StatusReport(Control(new UpdateRequiredStatus(false, "0.1.0", "0.2.0")));
        Valid(report).ShouldBeFalse();
    }

    private async Task<(int Code, string Output)> RunAsync(UpdateControl control, bool headless, params string[] args)
    {
        using var server = LocalControlServer.Bind(SocketPath);
        server.Start(control);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = headless
            ? await ServerProgram.RunAsync(args, new ServerProgramEnvironment
            {
                IsLinux = () => true,
                SocketPath = () => SocketPath,
                Output = o,
                Error = e,
            })
            : await ManagerCli.RunAsync(args, SocketPath, o, e);
        e.ToString().ShouldBeEmpty();
        return (code, o.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plain_status_prints_the_update_line_only_while_required(bool headless)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var (code, text) = await RunAsync(Control(new UpdateRequiredStatus(true, "0.1.0", "0.2.0")), headless, "status");
        code.ShouldBe(0);
        text.ShouldContain(Sentence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plain_status_has_no_update_line_when_not_required(bool headless)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var (code, text) = await RunAsync(Control(), headless, "status");
        code.ShouldBe(0);
        text.ShouldNotContain("Update required");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_status_carries_the_update_object_while_required(bool headless)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var (code, text) = await RunAsync(Control(new UpdateRequiredStatus(true, "0.1.0", "0.2.0")), headless, "status", "--json");
        code.ShouldBe(0);
        var result = JsonNode.Parse(text)!.AsObject();
        result["update"]!["required"]!.GetValue<bool>().ShouldBeTrue();
        result["update"]!["managerVersion"]!.GetValue<string>().ShouldBe("0.1.0");
        result["update"]!["minimumVersion"]!.GetValue<string>().ShouldBe("0.2.0");
        Valid(result).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_status_has_no_update_object_when_not_required(bool headless)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var (code, text) = await RunAsync(Control(), headless, "status", "--json");
        code.ShouldBe(0);
        var result = JsonNode.Parse(text)!.AsObject();
        result.ContainsKey("update").ShouldBeFalse();
        Valid(result).ShouldBeTrue();
    }
}
