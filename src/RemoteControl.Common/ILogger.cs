namespace RemoteControl.Common;

/// <summary>
/// Minimal logging seam so every project can log without pulling in a full
/// logging framework decision this early. <see cref="ConsoleLogger"/> is the
/// only implementation for now; swap for Microsoft.Extensions.Logging (or
/// similar) in Phase 5+ if structured/file logging is actually needed.
/// </summary>
public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
