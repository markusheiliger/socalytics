namespace SocAlytics.Platform.Application.Abstractions;

public enum OperationFailureKind
{
    Validation,
    NotFound,
    Forbidden,
    Conflict,
    VersionRequired,
    VersionMismatch,
    Unauthenticated,
    PasswordChangeRequired,
}

public sealed record FieldViolation(string Field, string Code);

public sealed record OperationFailure(
    OperationFailureKind Kind,
    string Code,
    IReadOnlyList<FieldViolation> FieldViolations,
    long? CurrentVersion)
{
    private static readonly IReadOnlyList<FieldViolation> NoViolations = [];

    public static OperationFailure Validation(params FieldViolation[] fieldViolations) =>
        Validation("validation-failed", fieldViolations);

    public static OperationFailure Validation(string code, params FieldViolation[] fieldViolations) =>
        new(OperationFailureKind.Validation, code, fieldViolations, null);

    public static OperationFailure CredentialInvalid() => Validation("credential-invalid");

    public static OperationFailure SignInFailed() =>
        new(OperationFailureKind.Unauthenticated, "sign-in-failed", NoViolations, null);

    public static OperationFailure NotFound() =>
        new(OperationFailureKind.NotFound, "not-found", NoViolations, null);

    public static OperationFailure Forbidden() =>
        new(OperationFailureKind.Forbidden, "forbidden", NoViolations, null);

    public static OperationFailure Conflict(string code) =>
        new(OperationFailureKind.Conflict, code, NoViolations, null);

    public static OperationFailure VersionRequired() =>
        new(OperationFailureKind.VersionRequired, "version-required", NoViolations, null);

    public static OperationFailure VersionMismatch(long? currentVersion) =>
        new(OperationFailureKind.VersionMismatch, "version-mismatch", NoViolations, currentVersion);

    public static OperationFailure Unauthenticated() =>
        new(OperationFailureKind.Unauthenticated, "unauthenticated", NoViolations, null);

    public static OperationFailure PasswordChangeRequired() =>
        new(OperationFailureKind.PasswordChangeRequired, "password-change-required", NoViolations, null);
}
