using System.Security.Cryptography;

namespace SocAlytics.Platform.StampSetup;

/// <summary>Generates stamp secrets from a cryptographic random source.</summary>
public static class StampSecrets
{
    public const int Length = 32;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Generate() => new(RandomNumberGenerator.GetItems<char>(Alphabet, Length));
}
