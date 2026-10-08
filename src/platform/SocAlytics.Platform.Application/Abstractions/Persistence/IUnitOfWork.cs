namespace SocAlytics.Platform.Application.Abstractions.Persistence;

public interface IUnitOfWork
{
    Task<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken);
}
