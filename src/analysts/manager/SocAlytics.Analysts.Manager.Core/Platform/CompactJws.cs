using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Platform;

public static class CompactJws
{
    public static string Sign(IDeviceKey key, JsonObject header, JsonObject payload)
    {
        var h = (JsonObject)header.DeepClone();
        h["alg"] = "ES256";
        var signingInput = $"{Encode(h)}.{Encode(payload)}";
        var signature = key.SignEs256(Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{PublicJwk.Base64Url(signature)}";
    }

    public static bool TryVerify(string jws, PublicJwk publicKey, out JsonObject header, out JsonObject payload)
    {
        header = new JsonObject();
        payload = new JsonObject();
        try
        {
            var parts = jws.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            var h = JsonNode.Parse(PublicJwk.FromBase64Url(parts[0]))?.AsObject();
            var p = JsonNode.Parse(PublicJwk.FromBase64Url(parts[1]))?.AsObject();
            if (h is null || p is null || (string?)h["alg"] != "ES256")
            {
                return false;
            }

            var signature = PublicJwk.FromBase64Url(parts[2]);
            using var ecdsa = ECDsa.Create(publicKey.ToParameters());
            var valid = signature.Length == EcdsaSignatureFormat.RawLength
                && ecdsa.VerifyData(
                    Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (valid)
            {
                header = h;
                payload = p;
            }

            return valid;
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Encode(JsonObject node) => PublicJwk.Base64Url(Encoding.UTF8.GetBytes(node.ToJsonString()));
}
