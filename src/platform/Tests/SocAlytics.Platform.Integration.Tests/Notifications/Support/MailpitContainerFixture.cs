using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

[assembly: AssemblyFixture(typeof(SocAlytics.Platform.Integration.Tests.Notifications.Support.MailpitContainerFixture))]

namespace SocAlytics.Platform.Integration.Tests.Notifications.Support;

public sealed class MailpitContainerFixture : IAsyncLifetime
{
    private const int SmtpContainerPort = 1025;
    private const int HttpContainerPort = 8025;

    private IContainer? _container;
    private HttpClient? _http;
    private int _smtpPort;
    private int _httpPort;

    public string SmtpHost => Container.Hostname;

    // The host ports stay fixed so the container can be stopped and started again at the same address.
    public int SmtpPort => _container is null ? NotStarted<int>() : _smtpPort;

    public async ValueTask InitializeAsync()
    {
        _smtpPort = GetFreePort();
        _httpPort = GetFreePort();

        // Fails visibly when Docker is unavailable; there is no fallback mail catcher.
        _container = new ContainerBuilder("axllent/mailpit:v1.31.5")
            .WithPortBinding(_smtpPort, SmtpContainerPort)
            .WithPortBinding(_httpPort, HttpContainerPort)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/api/v1/info").ForPort(HttpContainerPort)))
            .Build();
        await _container.StartAsync(CancellationToken.None);

        _http = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_httpPort}") };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default) =>
        await Container.StopAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Container.StartAsync(cancellationToken);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync("/api/v1/info", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException("The mail catcher did not become ready after the restart.");
    }

    public async Task<IReadOnlyList<MailpitMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        var list = await Http.GetFromJsonAsync<MailpitList>("/api/v1/messages", cancellationToken)
            ?? throw new InvalidOperationException("Mailpit returned no message list.");
        return list.Messages;
    }

    public async Task<IReadOnlyList<MailpitMessage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var list = await Http.GetFromJsonAsync<MailpitList>(
                $"/api/v1/search?query={Uri.EscapeDataString(query)}", cancellationToken)
            ?? throw new InvalidOperationException("Mailpit returned no search result.");
        return list.Messages;
    }

    public async Task<string> GetTextBodyAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var message = await Http.GetFromJsonAsync<MailpitMessageDetail>(
                $"/api/v1/message/{Uri.EscapeDataString(messageId)}", cancellationToken)
            ?? throw new InvalidOperationException("Mailpit returned no message.");
        return message.Text;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.DeleteAsync("/api/v1/messages", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<MailpitMessage>> WaitForMessagesAsync(
        string query, int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        while (true)
        {
            var messages = await SearchAsync(query, linked.Token);
            if (messages.Count >= count)
            {
                return messages;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Expected {count} message(s) for '{query}' but found {messages.Count}.");
            }
        }
    }

    private IContainer Container => _container ?? NotStarted<IContainer>();

    private HttpClient Http => _http ?? NotStarted<HttpClient>();

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static T NotStarted<T>() =>
        throw new InvalidOperationException("The mail catcher has not been started.");

    public sealed record MailpitAddress(
        [property: JsonPropertyName("Address")] string Address,
        [property: JsonPropertyName("Name")] string Name);

    public sealed record MailpitMessage(
        [property: JsonPropertyName("ID")] string Id,
        [property: JsonPropertyName("From")] MailpitAddress From,
        [property: JsonPropertyName("To")] IReadOnlyList<MailpitAddress> To,
        [property: JsonPropertyName("Subject")] string Subject);

    private sealed record MailpitList(
        [property: JsonPropertyName("messages")] IReadOnlyList<MailpitMessage> Messages);

    private sealed record MailpitMessageDetail([property: JsonPropertyName("Text")] string Text);
}
