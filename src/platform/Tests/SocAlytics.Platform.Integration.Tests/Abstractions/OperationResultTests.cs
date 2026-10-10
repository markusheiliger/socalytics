using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Abstractions;

public sealed class OperationResultTests
{
    [Fact]
    public void Success_carries_value()
    {
        var result = OperationResult<int>.Success(7);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(7);
        Should.Throw<InvalidOperationException>(() => result.Failure);
    }

    [Fact]
    public void Failure_carries_failure()
    {
        var result = OperationResult<int>.Fail(OperationFailure.NotFound());

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Kind.ShouldBe(OperationFailureKind.NotFound);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Implicit_conversions_work()
    {
        OperationResult<string> ok = "x";
        OperationResult<string> bad = OperationFailure.Forbidden();

        ok.IsSuccess.ShouldBeTrue();
        ok.Value.ShouldBe("x");
        bad.IsSuccess.ShouldBeFalse();
        bad.Failure.Code.ShouldBe("forbidden");
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void Factory_sets_kind_and_code(OperationFailure failure, OperationFailureKind kind, string code)
    {
        failure.Kind.ShouldBe(kind);
        failure.Code.ShouldBe(code);
    }

    public static TheoryData<OperationFailure, OperationFailureKind, string> Factories => new()
    {
        { OperationFailure.Validation(new FieldViolation("name", "required")), OperationFailureKind.Validation, "validation-failed" },
        { OperationFailure.Validation("custom-code", new FieldViolation("a", "invalid")), OperationFailureKind.Validation, "custom-code" },
        { OperationFailure.CredentialInvalid(), OperationFailureKind.Validation, "credential-invalid" },
        { OperationFailure.SignInFailed(), OperationFailureKind.Unauthenticated, "sign-in-failed" },
        { OperationFailure.NotFound(), OperationFailureKind.NotFound, "not-found" },
        { OperationFailure.Forbidden(), OperationFailureKind.Forbidden, "forbidden" },
        { OperationFailure.Conflict("season-already-active"), OperationFailureKind.Conflict, "season-already-active" },
        { OperationFailure.VersionRequired(), OperationFailureKind.VersionRequired, "version-required" },
        { OperationFailure.VersionMismatch(3), OperationFailureKind.VersionMismatch, "version-mismatch" },
        { OperationFailure.Unauthenticated(), OperationFailureKind.Unauthenticated, "unauthenticated" },
        { OperationFailure.PasswordChangeRequired(), OperationFailureKind.PasswordChangeRequired, "password-change-required" },
    };

    [Fact]
    public void Coded_validation_keeps_violations()
    {
        var failure = OperationFailure.Validation("custom-code", new FieldViolation("a", "invalid"));

        failure.FieldViolations.ShouldBe([new FieldViolation("a", "invalid")]);
        failure.CurrentVersion.ShouldBeNull();
    }

    [Fact]
    public void VersionMismatch_exposes_current_version()
    {
        OperationFailure.VersionMismatch(9).CurrentVersion.ShouldBe(9);
        OperationFailure.VersionMismatch(null).CurrentVersion.ShouldBeNull();
    }

    [Fact]
    public void Audit_outcomes_have_wire_values()
    {
        AuditOutcome.Refused.ToWireValue().ShouldBe("refused");
        AuditOutcome.Succeeded.ToWireValue().ShouldBe("succeeded");
    }
}
