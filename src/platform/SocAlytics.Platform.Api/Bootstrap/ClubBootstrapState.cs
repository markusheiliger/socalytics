using SocAlytics.Platform.Application.Club;

namespace SocAlytics.Platform.Api.Bootstrap;

public sealed class ClubBootstrapState
{
	private volatile int _outcome = -1;

	public BootstrapOutcome? LastOutcome
	{
		get
		{
			var value = _outcome;
			return value < 0 ? null : (BootstrapOutcome)value;
		}
	}

	public void Record(BootstrapOutcome outcome) => _outcome = (int)outcome;
}
