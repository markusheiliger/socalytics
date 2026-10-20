namespace SocAlytics.Analysts.Manager.Diagnostics;

/// <summary>The one build version of this Analyst Manager (MAJOR.MINOR.PATCH), taken from the Core assembly version.</summary>
public sealed class ManagerBuildVersion(string value)
{
    public static ManagerBuildVersion Current { get; } =
        new(typeof(ManagerBuildVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public string Value { get; } = value;

    public override string ToString() => Value;
}
