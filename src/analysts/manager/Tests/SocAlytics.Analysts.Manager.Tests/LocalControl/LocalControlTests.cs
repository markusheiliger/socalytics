using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.LocalControl;

public sealed class LocalControlTests : IDisposable
{
    private static readonly JsonSchema Schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "contracts", "local-control.schema.json"));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-lc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _disposables = [];

    private string SocketPath => Path.Combine(_dir, "run", "manager.sock");

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private LocalControlServer StartServer(FakeManagerControl? control = null, PeerCredentialVerifier? verifier = null)
    {
        var server = LocalControlServer.Bind(SocketPath, verifier);
        server.Start(control ?? new FakeManagerControl());
        _disposables.Add(server);
        return server;
    }

    private static bool Valid(JsonNode node) => Schema.Evaluate(JsonSerializer.SerializeToElement(node)).IsValid;

    private static async Task<JsonNode> RawAsync(string path, byte[] payload)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var token = TestContext.Current.CancellationToken;
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
        await socket.SendAsync(payload, token);
        var buffer = new byte[65536];
        var read = await socket.ReceiveAsync(buffer, token);
        return JsonNode.Parse(buffer.AsSpan(0, read))!;
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Socket_and_directory_are_owner_only()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix modes");
        StartServer();
        File.GetUnixFileMode(SocketPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(Path.GetDirectoryName(SocketPath)!).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Every_request_and_response_validates()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sockets");
        StartServer(new FakeManagerControl { RegisterFingerprint = "BCDF-GHJK" });
        var requests = new (string, JsonObject?)[]
        {
            ("status", null), ("show-status", null), ("resume", null), ("exit", null), ("unregister", new JsonObject { ["discardLocal"] = true }),
            ("pause", new JsonObject { ["durationMinutes"] = 5 }), ("pause", null), ("autostart", new JsonObject { ["enabled"] = true }),
            ("autostart", null),
            ("register", new JsonObject { ["platformEndpoint"] = "https://platform.example", ["hostLabel"] = "h" }),
        };
        foreach (var (command, args) in requests)
        {
            var request = new LocalControlRequest(1, command, args);
            Valid(JsonSerializer.SerializeToNode(request, LocalControlProtocol.Json)!).ShouldBeTrue(command);
            var response = await LocalControlClient.SendAsync(SocketPath, request, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            response.Ok.ShouldBeTrue(command);
            Valid(JsonSerializer.SerializeToNode(response, LocalControlProtocol.Json)!).ShouldBeTrue(command);
        }
    }

    [Fact]
    public async Task Oversized_and_wrong_version_requests_are_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sockets");
        StartServer();
        var big = Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"command\":\"status\",\"pad\":\"" + new string('a', 20000) + "\"}\n");
        var oversized = await RawAsync(SocketPath, big);
        oversized["error"]!["code"]!.GetValue<string>().ShouldBe("invalid-request");
        Valid(oversized).ShouldBeTrue();

        var wrong = await RawAsync(SocketPath, Encoding.UTF8.GetBytes("{\"protocolVersion\":2,\"command\":\"status\"}\n"));
        wrong["error"]!["code"]!.GetValue<string>().ShouldBe("unsupported-protocol-version");
    }

    [Fact]
    public async Task Peer_check_rejects_mismatched_uid_and_accepts_owner()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sockets");
        StartServer(verifier: new PeerCredentialVerifier(_ => 4242u, 4242u));
        (await LocalControlClient.SendAsync(SocketPath, new LocalControlRequest(1, "status"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Ok.ShouldBeTrue();

        File.Delete(SocketPath);
        _disposables.Clear();
        var rejecting = LocalControlServer.Bind(SocketPath, new PeerCredentialVerifier(_ => 1u, 4242u));
        rejecting.Start(new FakeManagerControl());
        _disposables.Add(rejecting);
        await Should.ThrowAsync<LocalControlUnavailableException>(() =>
            LocalControlClient.SendAsync(SocketPath, new LocalControlRequest(1, "status"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Second_instance_hands_off_with_show_status()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sockets");
        var control = new FakeManagerControl();
        var shown = new TaskCompletionSource();
        control.ShowStatusRequested += (_, _) => shown.TrySetResult();
        StartServer(control);
        (await SingleInstanceGuard.AcquireAsync(SocketPath)).ShouldBeNull();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stale_socket_is_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sockets");
        Directory.CreateDirectory(Path.GetDirectoryName(SocketPath)!);
        File.WriteAllText(SocketPath, string.Empty);
        File.Exists(SocketPath).ShouldBeTrue();
        var server = await SingleInstanceGuard.AcquireAsync(SocketPath);
        server.ShouldNotBeNull();
        _disposables.Add(server);
    }

    [Fact]
    public void Paths_follow_the_contract()
    {
        LocalControlPaths.ResolveLinux("/run/user/1", "/home/u").ShouldBe("/run/user/1/socalytics/manager.sock");
        LocalControlPaths.ResolveLinux(null, "/home/u").ShouldBe("/home/u/.local/state/socalytics/run/manager.sock");
        LocalControlPaths.ResolveMac("/Users/u", "/tmp/x").ShouldEndWith("run/manager.sock");
        LocalControlPaths.ResolveMac("/Users/" + new string('u', 90), "/tmp/x").ShouldBe("/tmp/x/socalytics/manager.sock");
    }
}
