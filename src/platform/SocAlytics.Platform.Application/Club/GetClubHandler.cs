using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using ClubEntity = SocAlytics.Platform.Domain.Club.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class GetClubHandler(IUnitOfWork unitOfWork, IClubHierarchyStore clubs)
{
    public async Task<OperationResult<ClubEntity>> HandleAsync(GetClubQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var club = await clubs.GetClubAsync(cancellationToken);
        return club is null ? OperationFailure.NotFound() : club;
    }
}
