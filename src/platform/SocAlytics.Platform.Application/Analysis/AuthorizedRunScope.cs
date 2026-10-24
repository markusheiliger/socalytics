namespace SocAlytics.Platform.Application.Analysis;

/// <summary>An analysis run the caller has already been authorized to read; only the authorizing handler creates it.</summary>
public sealed record AuthorizedRunScope(Guid RunId);
