using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Registry;

/// <summary>JSON Canonicalization Scheme (RFC 8785).</summary>
public static class CanonicalJson
{
    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Canonicalize(document.RootElement);
    }

    public static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(builder, element);
        return builder.ToString();
    }

    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "JSON numbers are finite.");
        }

        if (value == 0)
        {
            return "0";
        }

        var negative = value < 0;
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = text.IndexOf('E');
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.');
        var digits = text.Replace(".", "", StringComparison.Ordinal);
        var pointPosition = point < 0 ? digits.Length : point;
        var trimmedLeading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        var n = pointPosition - trimmedLeading + exponent;
        var k = digits.Length;

        string body;
        if (k <= n && n <= 21)
        {
            body = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            body = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            body = "0." + new string('0', -n) + digits;
        }
        else
        {
            var exp = n - 1;
            var sign = exp < 0 ? "-" : "+";
            var mantissa = k == 1 ? digits : digits[0] + "." + digits[1..];
            body = mantissa + "e" + sign + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
        }

        return negative ? "-" + body : body;
    }

    private static void Write(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, property.Name);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(element.GetDouble()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new ArgumentException("Unsupported JSON value.", nameof(element));
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                case < ' ':
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: builder.Append(c); break;
            }
        }

        builder.Append('"');
    }
}

public static class ContentDigest
{
    public static Sha256Digest OfCanonical(string canonicalJson) =>
        Sha256Digest.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    public static Sha256Digest Of(JsonElement element) => OfCanonical(CanonicalJson.Canonicalize(element));

    public static Sha256Digest Of(string json) => OfCanonical(CanonicalJson.Canonicalize(json));
}
