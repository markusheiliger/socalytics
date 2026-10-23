using System.Reflection;
using System.Text;

namespace SocAlytics.Platform.StampSetup;

/// <summary>Writes <c>stamp.env</c> from the embedded template with the setup values and secrets filled.</summary>
public static class StampEnvironmentFile
{
    public const string FileName = "stamp.env";

    private static readonly string[] GeneratedSecrets =
    [
        "SOCALYTICS_FIRST_CLUB_ADMIN_INITIAL_PASSWORD",
        "SOCALYTICS_POSTGRES_PASSWORD",
        "SOCALYTICS_MIGRATOR_DB_PASSWORD",
        "SOCALYTICS_APP_DB_PASSWORD",
        "SOCALYTICS_STORAGE_ACCESS_KEY",
        "SOCALYTICS_STORAGE_SECRET_KEY",
    ];

    public static string Render(string stampDirectory, StampNatsTrust trust)
    {
        var values = GeneratedSecrets.ToDictionary(name => name, _ => StampSecrets.Generate());
        values["SOCALYTICS_STAMP_DIR"] = stampDirectory;
        values["SOCALYTICS_NATS_OPERATOR_SEED"] = trust.OperatorSeed;
        values["SOCALYTICS_NATS_ACCOUNT_SEED"] = trust.AccountSeed;
        values["SOCALYTICS_NATS_SYSTEM_ACCOUNT_SEED"] = trust.SystemAccountSeed;

        var rendered = new StringBuilder();
        foreach (var line in ReadTemplate().Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var separator = trimmed.IndexOf('=');
            if (!trimmed.StartsWith('#') && separator > 0 && values.TryGetValue(trimmed[..separator], out var value))
            {
                rendered.Append(trimmed, 0, separator + 1).Append(value).Append('\n');
            }
            else
            {
                rendered.Append(trimmed).Append('\n');
            }
        }

        return rendered.ToString().TrimEnd('\n') + "\n";
    }

    public static void Write(string path, string content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private static string ReadTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("stamp.env.template")
            ?? throw new InvalidOperationException("The embedded stamp.env.template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
