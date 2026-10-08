using System.Globalization;
using System.Text.RegularExpressions;

namespace SocAlytics.Platform.Infrastructure.Persistence.Migrations;

internal sealed partial class MigrationScript
{
    public MigrationScript(string identity, string content)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!IdentityPattern().IsMatch(identity))
        {
            throw new MigrationCatalogException($"Migration name '{identity}' is malformed.");
        }

        var sequence = int.Parse(identity.AsSpan(0, 4), CultureInfo.InvariantCulture);
        if (sequence < 1)
        {
            throw new MigrationCatalogException($"Migration name '{identity}' has a sequence outside 1-9999.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new MigrationCatalogException($"Migration '{identity}' is empty.");
        }

        Sequence = sequence;
        Identity = identity;
        Area = identity.Split('_')[1];
        Content = content;
        Checksum = MigrationChecksum.Compute(content);
    }

    /// <summary>Four-digit prefix, <c>1</c>–<c>9999</c>.</summary>
    public int Sequence { get; }

    /// <summary>File name without <c>.sql</c>.</summary>
    public string Identity { get; }

    /// <summary>Second name segment.</summary>
    public string Area { get; }

    public string Checksum { get; }

    /// <summary>Script text. Never logged.</summary>
    public string Content { get; }

    [GeneratedRegex("^[0-9]{4}_[a-z][a-z0-9]*_[a-z0-9]+(_[a-z0-9]+)*$")]
    private static partial Regex IdentityPattern();
}
