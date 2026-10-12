using System.Text.Json;

namespace SocAlytics.Platform.Application.Abstractions;

public sealed record OutboxMessageDraft(
    Guid Id,
    string MessageType,
    string ContractVersion,
    string Subject,
    JsonElement Payload,
    Guid CausationId,
    Guid? RunId = null,
    Guid? LogicalJobId = null,
    string? CapabilityId = null);
