namespace SocAlytics.Platform.Application.Abstractions;

public sealed class OperationResult<T>
{
    private readonly T? value;
    private readonly OperationFailure? failure;

    private OperationResult(T? value, OperationFailure? failure)
    {
        this.value = value;
        this.failure = failure;
    }

    public bool IsSuccess => failure is null;

    public T Value => failure is null
        ? value!
        : throw new InvalidOperationException("A failed operation result has no value.");

    public OperationFailure Failure => failure
        ?? throw new InvalidOperationException("A successful operation result has no failure.");

    public static OperationResult<T> Success(T value) => new(value, null);

    public static OperationResult<T> Fail(OperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(default, failure);
    }

    public static implicit operator OperationResult<T>(T value) => Success(value);

    public static implicit operator OperationResult<T>(OperationFailure failure) => Fail(failure);
}
