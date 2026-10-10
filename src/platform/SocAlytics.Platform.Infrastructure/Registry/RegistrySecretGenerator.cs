using System.Security.Cryptography;
using System.Text;
using SocAlytics.Platform.Application.Registry.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class RegistrySecretGenerator : IRegistrySecretGenerator
{
    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";
    private const int CodeLength = 12;

    public string NewPairingCode() => RandomNumberGenerator.GetString(Alphabet, CodeLength);

    public string FormatPairingCode(string code)
    {
        var normalized = NormalizePairingCode(code);
        return string.Join('-', Enumerable.Range(0, normalized.Length / 4).Select(i => normalized.Substring(i * 4, 4)));
    }

    public string NormalizePairingCode(string? input) =>
        string.Concat((input ?? string.Empty).Where(char.IsAsciiLetterOrDigit)).ToUpperInvariant();

    public string NewPollingHandle() => RandomBase64Url();

    public string NewChallenge() => RandomBase64Url();

    public string NewAccessToken() => RandomBase64Url();

    public byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public byte[] HashPairingCode(string? input) => Hash(NormalizePairingCode(input));

    private static string RandomBase64Url() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
