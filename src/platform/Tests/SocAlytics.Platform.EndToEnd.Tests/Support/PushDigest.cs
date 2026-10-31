using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>Where the manifest digest of a push was determined, in the order the sources are tried.</summary>
internal enum DigestSource
{
    ProgressRecord,
    StatusLine,
    RegistryManifest,
    EngineImageRecord,
}

/// <summary>The determined digest and its source, or the failure with the body and the outcome of each tried source.</summary>
internal sealed record PushDigestResult(bool Succeeded, string? Digest, DigestSource? Source, string? FailureMessage)
{
    public static PushDigestResult Success(string digest, DigestSource source) => new(true, digest, source, null);

    public static PushDigestResult Failure(string message) => new(false, null, null, message);
}

/// <summary>Determines the manifest digest of a push from its response and, when that carries none, the registry and the engine.</summary>
internal static class PushDigest
{
    private static readonly Regex ValidDigest = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static readonly Regex StatusLine = new(@"^(?<tag>\S+): digest: (?<digest>\S+) size: \d+", RegexOptions.CultureInvariant);

    /// <summary>
    /// A lookup returns <c>null</c> when its source has no digest and throws when the source is unavailable.
    /// The first valid source ends the determination, except that the progress record and the status line must agree.
    /// </summary>
    public static async Task<PushDigestResult> DetermineAsync(
        string body,
        string repository,
        string tag,
        Func<CancellationToken, Task<string?>> registryLookup,
        Func<CancellationToken, Task<string?>> engineLookup,
        CancellationToken ct)
    {
        string? error = null;
        string? malformed = null;
        string? record = null;
        string? status = null;
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException e)
            {
                malformed ??= e.Message;
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    malformed ??= "a response line is not a JSON object";
                    continue;
                }

                if (root.TryGetProperty("errorDetail", out var detail))
                {
                    error ??= detail.GetRawText();
                }
                else if (root.TryGetProperty("error", out var message))
                {
                    error ??= message.GetRawText();
                }
                else if (root.TryGetProperty("aux", out var aux)
                    && aux.ValueKind == JsonValueKind.Object
                    && aux.TryGetProperty("Digest", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    record = value.GetString();
                }
                else if (root.TryGetProperty("status", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && StatusLine.Match(text.GetString()!) is { Success: true } match
                    && match.Groups["tag"].Value == tag)
                {
                    status = match.Groups["digest"].Value;
                }
            }
        }

        if (error is not null)
        {
            return PushDigestResult.Failure("The push reported an error: " + error);
        }

        if (malformed is not null)
        {
            return PushDigestResult.Failure("The push response is malformed (" + malformed + "):\n" + body);
        }

        var outcomes = new List<string>();
        var recordDigest = Read(DigestSource.ProgressRecord, record, outcomes);
        var statusDigest = Read(DigestSource.StatusLine, status, outcomes);
        if (recordDigest is not null && statusDigest is not null && recordDigest != statusDigest)
        {
            return PushDigestResult.Failure(
                $"The push reported conflicting manifest digests: the progress record {recordDigest} and the status line {statusDigest}.\n{Describe(outcomes, body)}");
        }

        if (recordDigest is not null)
        {
            return PushDigestResult.Success(recordDigest, DigestSource.ProgressRecord);
        }

        if (statusDigest is not null)
        {
            return PushDigestResult.Success(statusDigest, DigestSource.StatusLine);
        }

        var registryDigest = await LookUpAsync(DigestSource.RegistryManifest, registryLookup, outcomes, ct);
        if (registryDigest is not null)
        {
            return PushDigestResult.Success(registryDigest, DigestSource.RegistryManifest);
        }

        var engineDigest = await LookUpAsync(DigestSource.EngineImageRecord, engineLookup, outcomes, ct);
        if (engineDigest is not null)
        {
            return PushDigestResult.Success(engineDigest, DigestSource.EngineImageRecord);
        }

        return PushDigestResult.Failure($"The push did not report a manifest digest for {repository}:{tag}.\n{Describe(outcomes, body)}");
    }

    private static string? Read(DigestSource source, string? value, List<string> outcomes)
    {
        if (value is null)
        {
            outcomes.Add($"{source}: absent");
            return null;
        }

        if (!ValidDigest.IsMatch(value))
        {
            outcomes.Add($"{source}: unavailable: malformed digest");
            return null;
        }

        outcomes.Add($"{source}: found");
        return value;
    }

    private static async Task<string?> LookUpAsync(
        DigestSource source,
        Func<CancellationToken, Task<string?>> lookup,
        List<string> outcomes,
        CancellationToken ct)
    {
        string? value;
        try
        {
            value = await lookup(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            outcomes.Add($"{source}: unavailable: {e.Message}");
            return null;
        }

        return Read(source, value, outcomes);
    }

    private static string Describe(List<string> outcomes, string body)
    {
        var text = new StringBuilder();
        foreach (var outcome in outcomes)
        {
            text.AppendLine(outcome);
        }

        return text.Append("Response body:\n").Append(body).ToString();
    }
}
