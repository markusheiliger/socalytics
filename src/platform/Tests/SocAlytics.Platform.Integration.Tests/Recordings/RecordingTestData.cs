using System.Security.Cryptography;
using System.Text.Json;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

internal sealed record TestRecording(long PartSize, IReadOnlyList<byte[]> Parts)
{
	public long TotalSize => Parts.Sum(p => (long)p.Length);

	public string[] PartDigests => [.. Parts.Select(p => Sha256Digest.FromBytes(SHA256.HashData(p)).ToString())];

	public string ExpectedContentDigest =>
		CompositeContentDigest.FromPartDigests(PartSize, [.. Parts.Select(p => Sha256Digest.FromBytes(SHA256.HashData(p)))]).ToString();

	public object Declaration(string contentType = "video/mp4") => new
	{
		recording = new { displayName = "First half", contentType },
		totalSizeBytes = TotalSize,
		partSizeBytes = PartSize,
		partDigests = PartDigests,
	};
}

internal static class RecordingTestData
{
	public const long MinPart = 5_242_880;

	// Deterministic content: each byte is derived from the seed, part number, and offset.
	public static TestRecording Generate(int seed, params int[] partLengths) =>
		new(partLengths.Length > 1 ? partLengths[0] : partLengths[0], [.. partLengths.Select((len, i) => Part(seed, i + 1, len))]);

	public static byte[] Part(int seed, int partNumber, int length)
	{
		var bytes = new byte[length];
		for (var i = 0; i < length; i++)
		{
			bytes[i] = (byte)(seed * 31 + partNumber * 17 + i * 7);
		}

		return bytes;
	}

	public static async Task<HttpResponseMessage> PutPartAsync(HttpClient client, JsonElement grant, byte[] body, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Put, grant.GetProperty("url").GetString());
		request.Content = new ByteArrayContent(body);
		foreach (var header in grant.GetProperty("requiredHeaders").EnumerateObject())
		{
			if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
			{
				request.Content.Headers.ContentLength = long.Parse(header.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
			}
			else
			{
				request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
			}
		}

		return await client.SendAsync(request, cancellationToken);
	}
}
