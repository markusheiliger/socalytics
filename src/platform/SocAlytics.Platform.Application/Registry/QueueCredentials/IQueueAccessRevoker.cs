namespace SocAlytics.Platform.Application.Registry.QueueCredentials;

/// <summary>Pushes revoked queue credentials to the queue so connections using them are disconnected and refused.</summary>
public interface IQueueAccessRevoker
{
    /// <summary>
    /// Pushes every revoked, unexpired credential whose revocation is not yet acknowledged and marks them pushed.
    /// Returns the number of credentials marked pushed; throws when the queue does not acknowledge the push.
    /// </summary>
    Task<int> PushPendingAsync(CancellationToken cancellationToken);

    /// <summary>Like <see cref="PushPendingAsync"/>, but never throws: a failed push stays pending for the revocation worker.</summary>
    Task<bool> TryPushPendingAsync(CancellationToken cancellationToken);
}
