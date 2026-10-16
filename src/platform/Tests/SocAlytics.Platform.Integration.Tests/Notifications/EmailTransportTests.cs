using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Infrastructure.Email;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

[Collection(MailpitCollection.Name)]
public sealed class EmailTransportTests(MailpitContainerFixture mailpit)
{
    private const string Token = "tok-0123456789abcdef";

    [Fact]
    public async Task DeliversConfirmationWithContractHeadersAndOnlyTheContractData()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var unique = "msg-" + Guid.NewGuid().ToString("N");
        var rendered = new EmailRenderer().Render(
            "email-confirmation",
            new EmailRenderModel("Example FC", $"http://localhost:5173/email/confirm#token={Token}", TimeSpan.FromDays(1)));
        var transport = new MailKitEmailTransport(Options.Create(OptionsFor(mailpit.SmtpHost, mailpit.SmtpPort)));

        await transport.SendAsync(new OutboundEmail(unique, "member@example.test", rendered.Subject, rendered.TextBody, rendered.HtmlBody), token);

        var found = await mailpit.WaitForMessagesAsync($"subject:\"Confirm your email address for Example FC\" to:member@example.test", 1, TimeSpan.FromSeconds(30), token);
        var message = found.Last();
        message.From.Address.ShouldBe("socalytics@localhost");
        message.From.Name.ShouldBe("SocAlytics");
        message.To.Single().Address.ShouldBe("member@example.test");
        message.Subject.ShouldBe("Confirm your email address for Example FC");

        var headers = await mailpit.GetHeadersAsync(message.Id, token);
        headers["Auto-Submitted"].ShouldContain("auto-generated");
        headers["Message-Id"].Single().ShouldBe($"<{unique}@localhost>");

        var text = await mailpit.GetTextBodyAsync(message.Id, token);
        text.ShouldContain($"/email/confirm#token={Token}");
        text.ShouldContain("24 hours");
        text.ShouldNotContain("member@example.test");
    }

    [Fact]
    public void RenderedMessagesCarryOnlyTheirContractData()
    {
        var renderer = new EmailRenderer();

        var pending = renderer.Render("registration-pending", new EmailRenderModel(null, "http://localhost:5173/analyst-managers"));
        pending.Subject.ShouldBe("An Analyst Manager registration request waits for approval");
        foreach (var body in new[] { pending.TextBody, pending.HtmlBody })
        {
            body.ShouldContain("http://localhost:5173/analyst-managers");
            body.ShouldNotContain("{");
            body.ShouldNotContain("fingerprint", Case.Insensitive);
            body.Replace("http://localhost:5173/analyst-managers", string.Empty).ShouldNotContain("host", Case.Insensitive);
        }

        var confirmation = renderer.Render(
            "email-confirmation", new EmailRenderModel("A & B <FC>", $"http://localhost:5173/email/confirm#token={Token}", TimeSpan.FromHours(2)));
        confirmation.Subject.ShouldBe("Confirm your email address for A & B <FC>");
        confirmation.TextBody.ShouldContain("2 hours");
        confirmation.HtmlBody.ShouldContain("A &amp; B &lt;FC&gt;");
        confirmation.HtmlBody.ShouldNotContain("<FC>");
        confirmation.HtmlBody.ShouldNotContain("<img", Case.Insensitive);
        confirmation.TextBody.ShouldNotContain("{");

        Should.Throw<ArgumentException>(() => renderer.Render("unknown", new EmailRenderModel(null, "x")));
        Should.Throw<InvalidOperationException>(() => renderer.Render("email-confirmation", new EmailRenderModel(null, "x", TimeSpan.FromHours(1))));
    }

    [Theory]
    [InlineData("450 mailbox busy", EmailFailureKind.Transient, 450)]
    [InlineData("421 shutting down", EmailFailureKind.Transient, 421)]
    [InlineData("550 no such user", EmailFailureKind.Permanent, 550)]
    [InlineData("554 rejected", EmailFailureKind.Permanent, 554)]
    public async Task MapsRelayRejectionsToTransientOrPermanent(string reply, EmailFailureKind kind, int code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var relay = new RejectingRelay(reply);
        var transport = new MailKitEmailTransport(Options.Create(OptionsFor("127.0.0.1", relay.Port)));

        var failure = await Should.ThrowAsync<EmailTransportException>(
            async () => await transport.SendAsync(Outbound("member@example.test"), timeout.Token));

        failure.Kind.ShouldBe(kind);
        failure.SmtpStatusCode.ShouldBe(code);
        failure.Message.ShouldNotContain("member@example.test");
    }

    [Fact]
    public async Task MapsAnUnreachableRelayToTransient()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var transport = new MailKitEmailTransport(Options.Create(OptionsFor("127.0.0.1", FreePort())));

        var failure = await Should.ThrowAsync<EmailTransportException>(
            async () => await transport.SendAsync(Outbound("member@example.test"), timeout.Token));

        failure.Kind.ShouldBe(EmailFailureKind.Transient);
        failure.SmtpStatusCode.ShouldBeNull();
    }

    [Fact]
    public async Task MapsAnUnusableRecipientToPermanent()
    {
        var transport = new MailKitEmailTransport(Options.Create(OptionsFor("127.0.0.1", FreePort())));

        var failure = await Should.ThrowAsync<EmailTransportException>(
            async () => await transport.SendAsync(Outbound("not an address"), CancellationToken.None));

        failure.Kind.ShouldBe(EmailFailureKind.Permanent);
        failure.FailureCategory.ShouldBe(EmailFailureCategories.InvalidMessage);
    }

    [Theory]
    [InlineData("Development", "StartTlsWhenAvailable", true)]
    [InlineData("Production", "StartTlsWhenAvailable", false)]
    [InlineData("Production", "StartTls", true)]
    [InlineData("Production", "SslOnConnect", true)]
    public void OptionsRefuseOpportunisticTlsOutsideDevelopment(string environment, string security, bool valid)
    {
        var options = OptionsFor("relay.example.test", 587);
        options.Smtp.Security = Enum.Parse<SmtpSecurity>(security);

        new EmailOptionsValidator(new TestEnvironment(environment)).Validate(null, options).Succeeded.ShouldBe(valid);
    }

    [Fact]
    public void OptionsAreValidatedAtStart()
    {
        var validator = new EmailOptionsValidator(new TestEnvironment("Development"));
        validator.Validate(null, OptionsFor("relay.example.test", 587)).Succeeded.ShouldBeTrue();

        var options = OptionsFor(string.Empty, 0);
        options.SenderAddress = "nope";
        options.SenderName = " ";
        options.Smtp.UserName = "user";
        options.Delivery.MaxAttempts = 0;
        options.Confirmation.Lifetime = TimeSpan.Zero;

        var result = validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBe(7);
    }

    private static OutboundEmail Outbound(string to) => new("msg-1", to, "Subject", "Text", "<p>Html</p>");

    private static EmailOptions OptionsFor(string host, int port) => new()
    {
        Smtp = { Host = host, Port = port, Security = SmtpSecurity.StartTlsWhenAvailable },
        SenderAddress = "socalytics@localhost",
        SenderName = "SocAlytics",
    };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // A relay that greets, accepts the envelope sender and answers the recipient with the configured reply.
    private sealed class RejectingRelay : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _reply;
        private readonly CancellationTokenSource _stop = new();

        public RejectingRelay(string reply)
        {
            _reply = reply;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 relay ready");
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("250 relay");
                        }
                        else if (line.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("250 ok");
                        }
                        else if (line.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync(_reply);
                        }
                        else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("221 bye");
                            return;
                        }
                        else
                        {
                            await writer.WriteLineAsync("250 ok");
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
