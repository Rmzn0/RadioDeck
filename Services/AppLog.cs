using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace RadioDeck.Services;

/// <summary>
/// Application log: a bounded in-memory history for the log window plus a daily
/// file under %AppData%\RadioDeck\logs, so a dropped call can be analysed afterwards.
/// Safe to call from any thread; never throws.
/// </summary>
public sealed class AppLog : IDisposable
{
    public const int HistoryLimit = 3000;
    private const int RetentionDays = 30;

    private readonly object _sync = new();
    private readonly Queue<string> _history = new();
    private readonly BlockingCollection<string>? _pending;
    private readonly Thread? _writer;
    private int _disposed;

    /// <summary>Raised for every line, on the caller's thread.</summary>
    public event Action<string>? LineAdded;

    /// <summary>Folder holding the log files; null when file logging is off.</summary>
    public string? Directory { get; }

    /// <param name="directory">Folder for the daily files; null keeps the log in memory only.</param>
    public AppLog(string? directory)
    {
        if (directory == null) return;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            Directory = directory;
            _pending = new BlockingCollection<string>(new ConcurrentQueue<string>(), 20_000);
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Radio log writer" };
            _writer.Start();
        }
        catch
        {
            Directory = null;
            _pending = null;
        }
    }

    public void Write(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message.TrimEnd()}";
        lock (_sync)
        {
            _history.Enqueue(line);
            while (_history.Count > HistoryLimit) _history.Dequeue();
        }

        if (_pending is { IsAddingCompleted: false } pending)
        {
            try { pending.TryAdd(line); } catch (InvalidOperationException) { }
        }

        try { LineAdded?.Invoke(line); } catch { }
    }

    public void Write(string source, string message) => Write($"{source}: {message}");

    public string[] Snapshot()
    {
        lock (_sync) return _history.ToArray();
    }

    private void WriteLoop()
    {
        DeleteOldFiles();
        StreamWriter? writer = null;
        var day = DateTime.MinValue;
        try
        {
            while (true)
            {
                if (!_pending!.TryTake(out var line, 1000))
                {
                    try { writer?.Flush(); } catch { }
                    if (_pending.IsCompleted) break;
                    continue;
                }

                try
                {
                    if (writer == null || DateTime.Today != day)
                    {
                        writer?.Dispose();
                        day = DateTime.Today;
                        var path = Path.Combine(Directory!, $"radio-{day:yyyyMMdd}.log");
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                            new UTF8Encoding(false));
                        writer.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} log opened ----");
                    }
                    writer.WriteLine(line);
                }
                catch
                {
                    // Disk full or locked: drop the line, keep the radio running.
                    try { writer?.Dispose(); } catch { }
                    writer = null;
                }
            }
        }
        finally
        {
            try { writer?.Dispose(); } catch { }
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            var limit = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory!, "radio-*.log"))
                if (File.GetLastWriteTime(file) < limit)
                    File.Delete(file);
        }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _pending?.CompleteAdding(); } catch { }
        _writer?.Join(TimeSpan.FromSeconds(2));
    }
}
