namespace RemoteControl.Common;

public sealed class ConsoleLogger : ILogger
{
    private readonly string _component;

    public ConsoleLogger(string component) => _component = component;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message} -- {exception}");
    }

    private void Write(string level, string message) =>
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] [{level}] [{_component}] {message}");
}
