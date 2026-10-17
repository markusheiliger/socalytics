using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Notifications;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>The window in which a recipient receives at most one message of a coalescing type.</summary>
    public TimeSpan CoalescingWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The absolute origin (and optional path) of the Web UI the message links point to.</summary>
    public string WebUiBaseUri { get; set; } = string.Empty;

    public string LinkTo(string path) => WebUiBaseUri.TrimEnd('/') + path;
}

internal sealed class NotificationOptionsValidator : IValidateOptions<NotificationOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationOptions options)
    {
        var failures = new List<string>();
        if (options.CoalescingWindow <= TimeSpan.Zero)
        {
            failures.Add("Notifications:CoalescingWindow must be greater than zero.");
        }

        if (!Uri.TryCreate(options.WebUiBaseUri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            failures.Add("Notifications:WebUiBaseUri must be an absolute http or https address without query or fragment.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
