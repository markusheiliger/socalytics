using System.Text.Json;

namespace SocAlytics.Platform.Application.Abstractions;

public interface ICanonicalJson
{
    byte[] Canonicalize(JsonElement value);

    /// <summary>Returns <c>sha-256:</c> followed by 64 lowercase hex characters of the canonical bytes.</summary>
    string Fingerprint(JsonElement value);
}
