namespace SocAlytics.Platform.Domain.Analysis;

public readonly record struct FencingToken
{
	public const long MaxValue = 9007199254740991;

	public FencingToken(long value)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxValue);
		Value = value;
	}

	public long Value { get; }

	public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
