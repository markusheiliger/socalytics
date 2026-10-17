using System.Text.Json;

namespace SocAlytics.Analysts.Manager.Runtime;

public sealed record RuntimeCompatibilityEntry(
    string Profile,
    string RuntimeProduct,
    string RuntimeVersionRange,
    string Backend,
    string Os,
    string OsVersionRange,
    string Architecture,
    IReadOnlyList<string> Accelerators,
    string ManagerVersionRange);

public sealed class RuntimeCompatibilityList
{
    private const string ResourceName = "SocAlytics.Analysts.Manager.Runtime.runtime-compatibility.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RuntimeCompatibilityList(IEnumerable<RuntimeCompatibilityEntry> entries)
    {
        Entries = entries.ToArray();
    }

    public IReadOnlyList<RuntimeCompatibilityEntry> Entries { get; }

    public static RuntimeCompatibilityList LoadEmbedded()
    {
        using var stream = typeof(RuntimeCompatibilityList).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded runtime compatibility list is missing.");
        return Parse(stream);
    }

    /// <summary>The embedded list plus the entries of <paramref name="extraPath"/>, which is refused unless <paramref name="validationEnvironment"/> is true.</summary>
    public static RuntimeCompatibilityList Load(string? extraPath, bool validationEnvironment)
    {
        var embedded = LoadEmbedded();
        if (string.IsNullOrWhiteSpace(extraPath))
        {
            return embedded;
        }

        if (!validationEnvironment)
        {
            throw new InvalidOperationException(
                "Runtime:CompatibilityListPath is only available in the Development and Test environments; remove it.");
        }

        using var stream = File.OpenRead(extraPath);
        return new RuntimeCompatibilityList(embedded.Entries.Concat(Parse(stream).Entries));
    }

    public static RuntimeCompatibilityList Parse(Stream json) =>
        new(JsonSerializer.Deserialize<RuntimeCompatibilityEntry[]>(json, Json) ?? []);

    /// <summary>A runtime is supported only when an entry matches every dimension; never from the product name alone.</summary>
    public RuntimeCompatibilityEntry? Match(string profile, RuntimeProbe probe, Version managerVersion) =>
        Entries.FirstOrDefault(e =>
            e.Profile == profile
            && string.Equals(e.RuntimeProduct, probe.RuntimeProduct, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Backend, probe.Backend, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Os, probe.Os, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Architecture, probe.Architecture, StringComparison.OrdinalIgnoreCase)
            && InRange(e.RuntimeVersionRange, probe.RuntimeVersion)
            && InRange(e.OsVersionRange, probe.OsVersion)
            && InRange(e.ManagerVersionRange, managerVersion.ToString()));

    /// <summary>Space-separated comparators (>=, >, &lt;=, &lt;, =) or a bare exact version; "*" matches any known version.</summary>
    internal static bool InRange(string? range, string? version)
    {
        if (string.IsNullOrWhiteSpace(range) || !TryVersion(version, out var actual))
        {
            return false;
        }

        foreach (var token in range.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "*")
            {
                continue;
            }

            var op = token.TakeWhile(c => c is '<' or '>' or '=').Count();
            var symbol = token[..op];
            if (!TryVersion(token[op..], out var bound))
            {
                return false;
            }

            var cmp = actual.CompareTo(bound);
            var ok = symbol switch
            {
                ">=" => cmp >= 0,
                ">" => cmp > 0,
                "<=" => cmp <= 0,
                "<" => cmp < 0,
                "" or "=" => cmp == 0,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryVersion(string? text, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v').Split('-', '+')[0];
        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }
}
