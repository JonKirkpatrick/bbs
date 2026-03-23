using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Bbs.Client.Core.Logging;
using Bbs.Client.Infrastructure.Paths;

namespace Bbs.Client.Infrastructure.Logging;

public sealed class FileClientLogger : IClientLogger
{
    private static readonly object WriteLock = new();
    private readonly string _logPath;

    public FileClientLogger()
    {
        _logPath = AppPaths.GetLogFilePath();
        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void Log(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? fields = null)
    {
        var entry = new LogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Level = level,
            EventName = eventName,
            Message = message,
            Fields = fields
        };

        var payload = new Dictionary<string, object?>
        {
            ["ts_utc"] = entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            ["level"] = entry.Level.ToString(),
            ["event"] = entry.EventName,
            ["message"] = entry.Message,
            ["fields"] = entry.Fields
        };

        var line = JsonSerializer.Serialize(payload);

        lock (WriteLock)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
    }
}
