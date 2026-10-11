namespace SocAlytics.Platform.Application.Abstractions;

public interface ICurrentAnalystManager
{
    // The registration's client_id of the authenticated Analyst Manager.
    Guid ManagerId { get; }
}
