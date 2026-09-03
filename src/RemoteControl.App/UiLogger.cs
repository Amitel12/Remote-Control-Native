using System.Collections.Concurrent;
using System.Linq;
using RemoteControl.Common;

namespace RemoteControl.App;

/// <summary>
/// Queues log lines for the UI thread to drain on its own schedule instead of marshalling per
/// call -- a warning burst from the frame loop (malformed datagrams, dropped shards) would
/// otherwise flood the dispatcher with one BeginInvoke per line. <see cref="MainWindow"/> drains
/// <see cref="DrainTo"/> on a timer.
/// </summary>
public sealed class UiLogger : ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public void Info(string message) => Enqueue("INFO", message);
    public void Warn(string message) => Enqueue("WARN", message);
    public void Error(string message, Exception? exception = null) =>
        Enqueue("ERROR", exception is null ? message : $"{message} {exception}");

    private void Enqueue(string level, string message) =>
        _lines.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");

    public void DrainTo(ICollection<string> target, int maxLines)
    {
        while (_lines.TryDequeue(out var line))
        {
            target.Add(line);
            while (target.Count > maxLines)
                target.Remove(target.First());
        }
    }
}
