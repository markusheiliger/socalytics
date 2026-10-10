using SocAlytics.Analysts.Manager.Runtime;

namespace SocAlytics.Analysts.Manager.Tests.Support;

public sealed class SimulatedRuntimeAdapter : IContainerRuntimeAdapter
{
    public const string TestProfile = "simulated";

    public string Profile => TestProfile;

    public RuntimeProbe Probe { get; set; } = Healthy();

    public Exception? Failure { get; set; }

    public static ProbeCheck Ok => new(true);

    public static RuntimeProbe Healthy() => new("simrt", "2.1.0", "vm", "linux", "6.1.0", "x64",
        Ok, Ok, Ok, Ok, Ok, Ok, Ok, Ok, [new AcceleratorProbe("gpu0", true), new AcceleratorProbe("gpu1", true)]);

    public static RuntimeCompatibilityList List(string runtimeRange = ">=2.0.0 <3.0.0") => new(
    [
        new RuntimeCompatibilityEntry(TestProfile, "simrt", runtimeRange, "vm", "linux", "*", "x64",
            ["gpu0", "gpu1"], "*"),
    ]);

    public Task<RuntimeProbe> ProbeAsync(RuntimeProbeRequest request, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(Probe) : Task.FromException<RuntimeProbe>(Failure);
}
