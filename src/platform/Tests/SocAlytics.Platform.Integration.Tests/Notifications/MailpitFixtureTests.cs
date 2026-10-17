using System.Net.Sockets;
using System.Text;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

[Collection(MailpitCollection.Name)]
public sealed class MailpitFixtureTests(MailpitContainerFixture mailpit)
{
    [Fact]
    public async Task FixtureReceivesSearchesAndClearsMessagesAndSurvivesRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await mailpit.ClearAsync(token);

        var subject = "smoke-" + Guid.NewGuid().ToString("N");
        await SendAsync(subject, token);

        var found = await mailpit.WaitForMessagesAsync($"subject:{subject}", 1, TimeSpan.FromSeconds(30), token);
        found.Single().To.Single().Address.ShouldBe("member@example.test");
        (await mailpit.GetTextBodyAsync(found.Single().Id, token)).ShouldContain("smoke body");

        await mailpit.StopAsync(token);
        await Should.ThrowAsync<SocketException>(async () => await SendAsync("down", token));
        await mailpit.StartAsync(token);

        var afterRestart = "restart-" + Guid.NewGuid().ToString("N");
        await SendAsync(afterRestart, token);
        await mailpit.WaitForMessagesAsync($"subject:{afterRestart}", 1, TimeSpan.FromSeconds(30), token);

        await mailpit.ClearAsync(token);
        (await mailpit.GetMessagesAsync(token)).ShouldBeEmpty();
    }

    // A minimal SMTP dialogue keeps the smoke test independent of the production mail sender.
    private async Task SendAsync(string subject, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(mailpit.SmtpHost, mailpit.SmtpPort, cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await reader.ReadLineAsync(cancellationToken);
        foreach (var (command, expected) in new[]
        {
            ("EHLO test", "250"),
            ("MAIL FROM:<socalytics@localhost>", "250"),
            ("RCPT TO:<member@example.test>", "250"),
            ("DATA", "354"),
        })
        {
            await writer.WriteLineAsync(command.AsMemory(), cancellationToken);
            await ReadReplyAsync(reader, expected, cancellationToken);
        }

        await writer.WriteLineAsync(
            $"From: socalytics@localhost\r\nTo: member@example.test\r\nSubject: {subject}\r\n\r\nsmoke body\r\n.".AsMemory(),
            cancellationToken);
        await ReadReplyAsync(reader, "250", cancellationToken);
        await writer.WriteLineAsync("QUIT".AsMemory(), cancellationToken);
    }

    private static async Task ReadReplyAsync(StreamReader reader, string expected, CancellationToken cancellationToken)
    {
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cancellationToken);
            line.ShouldNotBeNull();
        }
        while (line.Length > 3 && line[3] == '-');

        line.ShouldStartWith(expected);
    }
}
