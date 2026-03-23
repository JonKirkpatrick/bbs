using System;
using System.Collections.Generic;

namespace Bbs.Client.Core.Logging;

public sealed class LogEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required LogLevel Level { get; init; }
    public required string EventName { get; init; }
    public required string Message { get; init; }
    public IReadOnlyDictionary<string, string>? Fields { get; init; }
}
