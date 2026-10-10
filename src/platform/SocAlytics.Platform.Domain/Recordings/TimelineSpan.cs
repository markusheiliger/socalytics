namespace SocAlytics.Platform.Domain.Recordings;

public readonly record struct TimelineSpan(
    long MediaStartMilliseconds,
    long MediaEndMilliseconds,
    long MatchStartMilliseconds);
