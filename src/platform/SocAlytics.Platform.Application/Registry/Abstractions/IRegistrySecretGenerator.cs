namespace SocAlytics.Platform.Application.Registry.Abstractions;

public interface IRegistrySecretGenerator
{
    // 12 characters of the pairing alphabet, unseparated.
    string NewPairingCode();

    // Display form XXXX-XXXX-XXXX.
    string FormatPairingCode(string code);

    // Uppercase, without separators or whitespace.
    string NormalizePairingCode(string? input);

    string NewPollingHandle();

    string NewChallenge();

    string NewAccessToken();

    byte[] Hash(string value);

    byte[] HashPairingCode(string? input);
}
