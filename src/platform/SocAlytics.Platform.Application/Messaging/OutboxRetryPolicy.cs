namespace SocAlytics.Platform.Application.Messaging;

public static class OutboxRetryPolicy
{
    public static TimeSpan Delay(OutboxPublicationOptions options, int attemptCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        var exponent = Math.Clamp(attemptCount - 1, 0, 30);
        var seconds = Math.Min((double)options.BackoffMaxSeconds, options.BackoffBaseSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}
