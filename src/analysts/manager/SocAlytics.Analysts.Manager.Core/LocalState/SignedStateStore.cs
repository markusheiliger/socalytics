using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.LocalState;

public sealed record StateReadResult(ProtectedRegistrationState? State, string? Code)
{
    public bool Succeeded => State is not null;
}

public sealed class SignedStateStore(string stateDirectory, IEnumerable<IDeviceKeyProvider> providers)
{
    public const string FileName = "registration.state";
    public const string Type = "socalytics-manager-state+jwt";
    private readonly IReadOnlyList<IDeviceKeyProvider> _providers = providers.ToList();

    public string Path { get; } = System.IO.Path.Combine(stateDirectory, FileName);

    public void Write(ProtectedRegistrationState state, IDeviceKey key)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["kid"] = key.Thumbprint, ["typ"] = Type };
        var signingInput = $"{PublicJwk.Base64Url(CanonicalJson.Serialize(header))}.{PublicJwk.Base64Url(CanonicalJson.Serialize(state.ToJson()))}";
        var signature = key.SignEs256(Encoding.ASCII.GetBytes(signingInput));
        OwnerOnlyFiles.WriteAtomic(Path, Encoding.ASCII.GetBytes($"{signingInput}.{PublicJwk.Base64Url(signature)}"));
    }

    public async Task<StateReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return Fail(ManagerDiagnosticCodes.StateMissing);
        }

        string text;
        try
        {
            if (!OwnerOnlyFiles.IsOwnerOnly(Path))
            {
                return Fail(ManagerDiagnosticCodes.StatePermissions);
            }

            text = (await File.ReadAllTextAsync(Path, Encoding.ASCII, cancellationToken)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Fail(ManagerDiagnosticCodes.StateUnreadable);
        }

        ProtectedRegistrationState? state;
        string kid;
        try
        {
            var parts = text.Split('.');
            var header = JsonNode.Parse(PublicJwk.FromBase64Url(parts[0]))!.AsObject();
            var payload = JsonNode.Parse(PublicJwk.FromBase64Url(parts[1]))!.AsObject();
            if (parts.Length != 3 || (string?)header["typ"] != Type || (string?)header["alg"] != "ES256")
            {
                return Fail(ManagerDiagnosticCodes.StateUnreadable);
            }

            kid = (string?)header["kid"] ?? string.Empty;
            state = ProtectedRegistrationState.TryParse(payload);
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return Fail(ManagerDiagnosticCodes.StateUnreadable);
        }

        if (state is null)
        {
            return Fail(ManagerDiagnosticCodes.StateUnreadable);
        }

        var provider = _providers.FirstOrDefault(p => p.Id == state.DeviceKey.Provider);
        if (provider is null)
        {
            return Fail(ManagerDiagnosticCodes.KeyMissing);
        }

        IDeviceKey key;
        try
        {
            key = await provider.OpenKeyAsync(state.DeviceKey, cancellationToken);
        }
        catch (DeviceKeyException e)
        {
            return Fail(e.Code == ManagerDiagnosticCodes.KeyMissing
                ? ManagerDiagnosticCodes.KeyMissing
                : ManagerDiagnosticCodes.KeyInaccessible);
        }

        if (!CompactJws.TryVerify(text, key.PublicJwk, out _, out _))
        {
            return Fail(ManagerDiagnosticCodes.StateSignature);
        }

        return state.DeviceKeyThumbprint == kid && kid == key.Thumbprint
            ? new StateReadResult(state, null)
            : Fail(ManagerDiagnosticCodes.KeyMismatch);
    }

    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    private static StateReadResult Fail(string code) => new(null, code);
}
