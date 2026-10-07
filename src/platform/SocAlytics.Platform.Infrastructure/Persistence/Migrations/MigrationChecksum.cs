using System.Security.Cryptography;
using System.Text;

namespace SocAlytics.Platform.Infrastructure.Persistence.Migrations;

internal static class MigrationChecksum
{
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalized = content.TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "sha-256:" + Convert.ToHexStringLower(hash);
    }
}
