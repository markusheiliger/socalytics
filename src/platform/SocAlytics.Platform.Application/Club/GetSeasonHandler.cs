using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class GetSeasonHandler(IUnitOfWork unitOfWork, IClubHierarchyStore clubs)
{
    public async Task<OperationResult<Season>> HandleAsync(GetSeasonQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var season = await clubs.GetSeasonAsync(query.SeasonId, cancellationToken);
        return season is null ? OperationFailure.NotFound() : season;
    }
}
