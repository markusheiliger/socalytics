using System.Net;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal sealed record StoredPut(Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Bytes, bool HadAuthorization);

/// <summary>In-process object storage that records every PUT and answers with scripted statuses.</summary>
internal sealed class FakeStorage : HttpMessageHandler
{
    public List<StoredPut> Puts { get; } = [];

    /// <summary>Answers the next PUTs in order; once empty, PUTs succeed.</summary>
    public Queue<HttpStatusCode> Scripted { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Puts)
        {
            Puts.Add(new StoredPut(request.RequestUri!, headers, bytes, request.Headers.Authorization is not null));
        }

        return new HttpResponseMessage(Scripted.TryDequeue(out var status) ? status : HttpStatusCode.OK);
    }
}
