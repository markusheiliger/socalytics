using Microsoft.Extensions.Logging;

namespace SocAlytics.Platform.Migrator;

internal static partial class MigratorLog
{
    public const string CategoryName = "SocAlytics.Platform.Migrator";

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Migration run started; {RegisteredCount} migrations registered")]
    public static partial void RunStarted(ILogger logger, int registeredCount);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Waiting for the migration lock held by another run")]
    public static partial void WaitingForLock(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Migration lock acquired")]
    public static partial void LockAcquired(ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Applied migrations unknown to this release were left untouched: {Identities}")]
    public static partial void UnknownApplied(ILogger logger, string identities);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Applying migration {Identity}")]
    public static partial void Applying(ILogger logger, string identity);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Applied migration {Identity}")]
    public static partial void Applied(ILogger logger, string identity);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "Migration run finished; {AppliedCount} applied, database current")]
    public static partial void RunFinished(ILogger logger, int appliedCount);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Error, Message = "Migration run failed: {Category} (identity {Identity}, SQLSTATE {SqlState}, exception {ExceptionType})")]
    public static partial void Failed(ILogger logger, string category, string? identity = null, string? sqlState = null, string? exceptionType = null);
}
