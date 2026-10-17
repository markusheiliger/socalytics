using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications.Support;

// Some tests stop the shared mail catcher, so every class that uses it runs sequentially.
[CollectionDefinition(Name)]
public sealed class MailpitCollection
{
	public const string Name = "Mailpit";
}
