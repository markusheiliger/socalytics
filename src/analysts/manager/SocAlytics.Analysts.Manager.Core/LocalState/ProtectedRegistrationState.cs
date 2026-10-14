using System.Globalization;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Operating;

namespace SocAlytics.Analysts.Manager.LocalState;

public sealed record ProtectedRegistrationState(
    int SchemaVersion,
    string ManagerId,
    string StampId,
    string PlatformEndpoint,
    DeviceKeyReference DeviceKey,
    KeyProtection DeviceKeyProtection,
    string DeviceKeyThumbprint,
    OperatingIntent Intent,
    bool PendingUnregister,
    DateTimeOffset WrittenAt,
    int HostLocalConcurrency = 1)
{
    public const int CurrentSchemaVersion = 1;

    public JsonObject ToJson() => new()
    {
        ["schemaVersion"] = SchemaVersion,
        ["managerId"] = ManagerId,
        ["stampId"] = StampId,
        ["platformEndpoint"] = PlatformEndpoint,
        ["deviceKey"] = new JsonObject
        {
            ["provider"] = DeviceKey.Provider,
            ["reference"] = DeviceKey.Reference,
            ["protection"] = DeviceKeyProtection.ToWireValue(),
        },
        ["deviceKeyThumbprint"] = DeviceKeyThumbprint,
        ["operatingIntent"] = Intent.Kind.ToString(),
        ["pausedUntil"] = Intent.EndUtc?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
        ["pendingUnregister"] = PendingUnregister,
        ["hostLocalConcurrency"] = HostLocalConcurrency,
        ["writtenAt"] = WrittenAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
    };

    /// <summary>Returns null for any malformed, unknown-version, or non-https payload (fail closed).</summary>
    public static ProtectedRegistrationState? TryParse(JsonObject json)
    {
        try
        {
            if ((int?)json["schemaVersion"] != CurrentSchemaVersion)
            {
                return null;
            }

            var endpoint = (string?)json["platformEndpoint"];
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            var key = json["deviceKey"]!.AsObject();
            DateTimeOffset? pausedUntil = json["pausedUntil"] is { } p
                ? DateTimeOffset.Parse((string)p!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                : null;
            var intent = (string?)json["operatingIntent"] switch
            {
                "Running" when pausedUntil is null => OperatingIntent.Running,
                "Paused" when pausedUntil is null => OperatingIntent.Paused,
                "PausedUntil" when pausedUntil is not null => OperatingIntent.PausedUntil(pausedUntil.Value),
                _ => null,
            };
            if (intent is null)
            {
                return null;
            }

            var hostLocal = json["hostLocalConcurrency"] is { } h ? (int)h : 1;
            if (hostLocal < 1)
            {
                return null;
            }

            var pending = (bool)json["pendingUnregister"]!;
            return new ProtectedRegistrationState(
                CurrentSchemaVersion,
                Required(json, "managerId"),
                Required(json, "stampId"),
                endpoint!,
                new DeviceKeyReference(Required(key, "provider"), Required(key, "reference")),
                KeyProtectionExtensions.FromWireValue(Required(key, "protection")),
                Required(json, "deviceKeyThumbprint"),
                intent with { PendingUnregister = pending },
                pending,
                DateTimeOffset.Parse(Required(json, "writtenAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                hostLocal);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or NullReferenceException or ArgumentException)
        {
            return null;
        }
    }

    private static string Required(JsonObject obj, string name) =>
        (string?)obj[name] is { Length: > 0 } value ? value : throw new FormatException(name);
}
