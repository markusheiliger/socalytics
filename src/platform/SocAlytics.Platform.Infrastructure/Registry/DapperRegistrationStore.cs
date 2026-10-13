using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class DapperRegistrationStore(IDbSession session, IOptions<RegistryOptions> options) : IRegistrationStore
{
    private const string Columns =
        "id, manager_id, stamp_id, state, public_jwk::text AS public_jwk, key_thumbprint, device_fingerprint, " +
        "fingerprint_mismatch_count, pairing_source_address, pairing_nonce, device_metadata::text AS device_metadata, " +
        "challenge_hash, challenge_expires_at, pairing_expires_at, approval_expires_at, activation_expires_at, " +
        "submitted_by, submitted_at, decided_by, decided_at, decision_reason, activated_at, unregistration_started_at, " +
        "ended_at, revoked_by, end_reason, correlation_id, created_at, version";

    private sealed record Row(
        Guid Id, Guid? ManagerId, string StampId, string State, string PublicJwk, string KeyThumbprint,
        string DeviceFingerprint, int FingerprintMismatchCount, string? PairingSourceAddress, string PairingNonce,
        string DeviceMetadata, byte[]? ChallengeHash, DateTime? ChallengeExpiresAt, DateTime PairingExpiresAt,
        DateTime? ApprovalExpiresAt, DateTime? ActivationExpiresAt, Guid? SubmittedBy, DateTime? SubmittedAt,
        Guid? DecidedBy, DateTime? DecidedAt, string? DecisionReason, DateTime? ActivatedAt,
        DateTime? UnregistrationStartedAt, DateTime? EndedAt, Guid? RevokedBy, string? EndReason,
        string CorrelationId, DateTime CreatedAt, long Version);

    public async Task InsertAsync(
        AnalystManagerRegistration r, byte[]? pairingCodeHash, byte[] pollingHandleHash, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analyst_manager_registration (id, manager_id, stamp_id, state, public_jwk, key_thumbprint, " +
            "device_fingerprint, fingerprint_mismatch_count, pairing_source_address, pairing_nonce, device_metadata, " +
            "pairing_code_hash, polling_handle_hash, challenge_hash, challenge_expires_at, pairing_expires_at, " +
            "approval_expires_at, activation_expires_at, submitted_by, submitted_at, decided_by, decided_at, decision_reason, " +
            "activated_at, unregistration_started_at, ended_at, revoked_by, end_reason, correlation_id, created_at) VALUES " +
            "(@Id, @ManagerId, @StampId, @State, @PublicJwk::jsonb, @KeyThumbprint, @Fingerprint, @Mismatch, @Source, @Nonce, " +
            "@Metadata::jsonb, @CodeHash, @HandleHash, @ChallengeHash, @ChallengeExpiresAt, @PairingExpiresAt, " +
            "@ApprovalExpiresAt, @ActivationExpiresAt, @SubmittedBy, @SubmittedAt, @DecidedBy, @DecidedAt, @DecisionReason, " +
            "@ActivatedAt, @UnregistrationStartedAt, @EndedAt, @RevokedBy, @EndReason, @CorrelationId, @CreatedAt)",
            new
            {
                r.Id, r.ManagerId, r.StampId, State = r.State.ToApiName(), r.PublicJwk, r.KeyThumbprint,
                Fingerprint = r.DeviceFingerprint.Value, Mismatch = r.FingerprintMismatchCount,
                Source = r.PairingSourceAddress, Nonce = r.PairingNonce, Metadata = SerializeMetadata(r.DeviceMetadata),
                CodeHash = pairingCodeHash, HandleHash = pollingHandleHash, r.ChallengeHash, r.ChallengeExpiresAt,
                r.PairingExpiresAt, r.ApprovalExpiresAt, r.ActivationExpiresAt, r.SubmittedBy, r.SubmittedAt,
                r.DecidedBy, r.DecidedAt, r.DecisionReason, r.ActivatedAt, r.UnregistrationStartedAt, r.EndedAt,
                r.RevokedBy, r.EndReason, r.CorrelationId, r.CreatedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<AnalystManagerRegistration?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        FindAsync("id = @key", new { key = id }, cancellationToken);

    public Task<AnalystManagerRegistration?> FindByPairingCodeHashAsync(byte[] hash, CancellationToken cancellationToken) =>
        FindAsync("pairing_code_hash = @key", new { key = hash }, cancellationToken);

    public Task<AnalystManagerRegistration?> FindByPollingHandleHashAsync(byte[] hash, CancellationToken cancellationToken) =>
        FindAsync("polling_handle_hash = @key", new { key = hash }, cancellationToken);

    public Task<AnalystManagerRegistration?> FindByManagerIdAsync(Guid managerId, CancellationToken cancellationToken) =>
        FindAsync("manager_id = @key", new { key = managerId }, cancellationToken);

    public Task<AnalystManagerRegistration?> FindNonTerminalByThumbprintAsync(string thumbprint, CancellationToken cancellationToken) =>
        FindAsync(
            "key_thumbprint = @key AND state IN ('pairing', 'pending_approval', 'approved', 'active', 'unregistering')",
            new { key = thumbprint }, cancellationToken);

    public Task<AnalystManagerRegistration?> FindLatestInactiveByThumbprintAsync(string thumbprint, CancellationToken cancellationToken) =>
        FindAsync(
            "key_thumbprint = @key AND state IN ('revoked', 'unregistered') ORDER BY ended_at DESC NULLS LAST, created_at DESC LIMIT 1",
            new { key = thumbprint }, cancellationToken);

    public Task<VersionedWriteResult> UpdateAsync(
        AnalystManagerRegistration r, RegistrationState expectedState, bool clearPairingCode, CancellationToken cancellationToken) =>
        GuardedAsync(
            "manager_id = @ManagerId, state = @State, fingerprint_mismatch_count = @Mismatch, challenge_hash = @ChallengeHash, " +
            "challenge_expires_at = @ChallengeExpiresAt, approval_expires_at = @ApprovalExpiresAt, " +
            "activation_expires_at = @ActivationExpiresAt, submitted_by = @SubmittedBy, submitted_at = @SubmittedAt, " +
            "decided_by = @DecidedBy, decided_at = @DecidedAt, decision_reason = @DecisionReason, activated_at = @ActivatedAt, " +
            "unregistration_started_at = @UnregistrationStartedAt, ended_at = @EndedAt, revoked_by = @RevokedBy, " +
            "end_reason = @EndReason" + (clearPairingCode ? ", pairing_code_hash = NULL" : string.Empty),
            new
            {
                r.Id, Expected = expectedState.ToApiName(), r.ManagerId, State = r.State.ToApiName(),
                Mismatch = r.FingerprintMismatchCount, r.ChallengeHash, r.ChallengeExpiresAt, r.ApprovalExpiresAt,
                r.ActivationExpiresAt, r.SubmittedBy, r.SubmittedAt, r.DecidedBy, r.DecidedAt, r.DecisionReason,
                r.ActivatedAt, r.UnregistrationStartedAt, r.EndedAt, r.RevokedBy, r.EndReason,
            },
            cancellationToken);

    public Task<VersionedWriteResult> IncrementFingerprintMismatchAsync(
        Guid id, RegistrationState expectedState, CancellationToken cancellationToken) =>
        GuardedAsync(
            "fingerprint_mismatch_count = fingerprint_mismatch_count + 1",
            new { Id = id, Expected = expectedState.ToApiName() }, cancellationToken);

    public Task<VersionedWriteResult> SetChallengeAsync(
        Guid id, RegistrationState expectedState, byte[] challengeHash, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        GuardedAsync(
            "challenge_hash = @Hash, challenge_expires_at = @ExpiresAt",
            new { Id = id, Expected = expectedState.ToApiName(), Hash = challengeHash, ExpiresAt = expiresAt },
            cancellationToken);

    public Task<VersionedWriteResult> ClearChallengeAsync(
        Guid id, RegistrationState expectedState, CancellationToken cancellationToken) =>
        GuardedAsync(
            "challenge_hash = NULL, challenge_expires_at = NULL",
            new { Id = id, Expected = expectedState.ToApiName() }, cancellationToken);

    public async Task<RegistrationPage> ListAsync(
        RegistrationState? state, Guid? submittedBy, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var connection = await session.GetConnectionAsync(cancellationToken);
        var cursor = DecodeToken(continuationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.analyst_manager_registration " +
            "WHERE (@State::text IS NULL OR state = @State) AND (@SubmittedBy::uuid IS NULL OR submitted_by = @SubmittedBy::uuid) " +
            "AND (@CursorId::uuid IS NULL OR (created_at, id) < (@CursorCreated::timestamptz, @CursorId::uuid)) " +
            "ORDER BY created_at DESC, id DESC LIMIT @Take",
            new
            {
                State = state?.ToApiName(),
                SubmittedBy = submittedBy,
                CursorId = cursor?.Id,
                CursorCreated = cursor?.CreatedAt,
                Take = pageSize + 1,
            },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();

        var page = rows.Take(pageSize).ToList();
        var next = rows.Count > pageSize ? EncodeToken(page[^1]) : null;
        return new RegistrationPage([.. page.Select(Map)], next);
    }

    public async Task<IReadOnlyList<AnalystManagerRegistration>> ListDueForExpiryAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.analyst_manager_registration WHERE " +
            "(state = 'pairing' AND pairing_expires_at <= @now) OR " +
            "(state = 'pending_approval' AND approval_expires_at <= @now) OR " +
            "(state = 'approved' AND activation_expires_at <= @now) ORDER BY created_at, id LIMIT @limit",
            new { now, limit },
            session.Transaction,
            cancellationToken: cancellationToken));
        return [.. rows.Select(Map)];
    }

    private async Task<AnalystManagerRegistration?> FindAsync(string predicate, object parameters, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.analyst_manager_registration WHERE {predicate}",
            parameters,
            session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    private Task<VersionedWriteResult> GuardedAsync(
        string assignments, object parameters, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                $"UPDATE socalytics.analyst_manager_registration SET {assignments} " +
                "WHERE id = @Id AND state = @Expected RETURNING version",
                parameters,
                transaction,
                cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.analyst_manager_registration WHERE id = @Id",
                parameters,
                transaction,
                cancellationToken: cancellationToken));
    }

    private AnalystManagerRegistration Map(Row r)
    {
        RegistrationStateExtensions.TryParseApiName(r.State, out var state);
        var pairing = options.Value.Pairing;
        var windows = new RegistrationWindows(pairing.Window, pairing.ApprovalWindow, pairing.ActivationWindow, pairing.ChallengeLifetime);
        return AnalystManagerRegistration.Rehydrate(
            r.Id, r.ManagerId, r.StampId, state, r.PublicJwk, r.KeyThumbprint,
            DeviceFingerprint.FromStored(r.DeviceFingerprint), r.FingerprintMismatchCount, r.PairingSourceAddress,
            r.PairingNonce, ParseMetadata(r.DeviceMetadata), r.ChallengeHash, Utc(r.ChallengeExpiresAt),
            Utc(r.PairingExpiresAt), Utc(r.ApprovalExpiresAt), Utc(r.ActivationExpiresAt), r.SubmittedBy,
            Utc(r.SubmittedAt), r.DecidedBy, Utc(r.DecidedAt), r.DecisionReason, Utc(r.ActivatedAt),
            Utc(r.UnregistrationStartedAt), Utc(r.EndedAt), r.RevokedBy, r.EndReason, r.CorrelationId,
            Utc(r.CreatedAt), windows).WithVersion(r.Version);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static string SerializeMetadata(DeviceMetadata m) => JsonSerializer.Serialize(new Dictionary<string, string?>
    {
        ["os"] = m.Os,
        ["osVersion"] = m.OsVersion,
        ["architecture"] = m.Architecture,
        ["managerVersion"] = m.ManagerVersion,
        ["claimedKeyProtection"] = m.ClaimedKeyProtection.ToApiName(),
        ["hostLabel"] = m.HostLabel,
    }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

    private static DeviceMetadata ParseMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
        if (!DeviceMetadata.TryCreate(
                Get("os"), Get("osVersion"), Get("architecture"), Get("managerVersion"),
                Get("claimedKeyProtection"), Get("hostLabel"), out var metadata, out _))
        {
            throw new InvalidOperationException("Stored device metadata is invalid.");
        }

        return metadata!;
    }

    private static string EncodeToken(Row row) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        row.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + row.Id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (DateTime CreatedAt, Guid Id)? DecodeToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            return (new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new ArgumentException("Invalid continuation token.", nameof(token), ex);
        }
    }
}
