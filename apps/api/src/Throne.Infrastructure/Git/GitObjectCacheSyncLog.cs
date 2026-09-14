using Microsoft.Extensions.Logging;

namespace Throne.Infrastructure.Git;

/// <summary>
/// LoggerMessage source-generator host for <see cref="GitObjectCacheSync"/>.
/// </summary>
internal static partial class GitObjectCacheSyncLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "git object cache fetch failed for {FullName} at {CachePath}: {Stderr}. Reusing whatever is on disk.")]
    public static partial void FetchFailed(ILogger logger, string fullName, string cachePath, string stderr);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "git object cache bare clone failed for {FullName} at {CachePath}: {Stderr}. Clone will fall back to network.")]
    public static partial void CloneFailed(ILogger logger, string fullName, string cachePath, string stderr);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "git object cache sync raised unexpectedly for {FullName}; clone will fall back to network.")]
    public static partial void Unexpected(ILogger logger, string fullName, Exception exception);
}
