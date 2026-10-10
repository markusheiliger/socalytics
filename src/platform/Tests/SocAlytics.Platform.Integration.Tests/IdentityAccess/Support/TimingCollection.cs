using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
	public const string Name = "Timing";
}
