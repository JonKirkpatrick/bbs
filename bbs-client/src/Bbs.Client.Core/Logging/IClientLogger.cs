namespace Bbs.Client.Core.Logging;

public interface IClientLogger
{
    void Log(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? fields = null);
}
