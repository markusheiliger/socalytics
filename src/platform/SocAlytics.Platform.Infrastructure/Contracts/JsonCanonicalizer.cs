using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Contracts;

/// <summary>RFC 8785 canonicalization restricted to integer numbers.</summary>
internal sealed class JsonCanonicalizer : ICanonicalJson
{
    public byte[] Canonicalize(JsonElement value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public string Fingerprint(JsonElement value) =>
        "sha-256:" + Convert.ToHexString(SHA256.HashData(Canonicalize(value))).ToLowerInvariant();

    private static void Write(StringBuilder sb, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(sb, p.Name);
                    sb.Append(':');
                    Write(sb, p.Value);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    Write(sb, item);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(sb, e.GetString()!);
                break;
            case JsonValueKind.Number:
                if (!e.TryGetInt64(out var n) || Math.Abs(n) > 9007199254740991L)
                {
                    throw new NotSupportedException("Canonical JSON supports only integers within the IEEE 754 safe range.");
                }

                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                throw new NotSupportedException($"Unsupported JSON value kind {e.ValueKind}.");
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
