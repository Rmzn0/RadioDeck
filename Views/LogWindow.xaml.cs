using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using RadioDeck.Services;
using RadioDeck.Services.Localization;

namespace RadioDeck.Views;

/// <summary>Live view of the application log; the same lines are written to the daily log file.</summary>
public partial class LogWindow : Window
{
    private readonly AppLog _log;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flush;
    private int _lines;

    public LogWindow(AppLog log)
    {
        InitializeComponent();
        _log = log;

        Reload();
        FolderText.Text = log.Directory == null ? Loc.T("Log_NotOnDisk") : Loc.T("Log_Files", log.Directory);
        FolderButton.IsEnabled = log.Directory != null;

        // Lines arrive from network and audio threads; show them in batches.
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        _log.LineAdded += OnLine;
        Closed += (_, _) =>
        {
            _log.LineAdded -= OnLine;
            _flush.Stop();
        };
    }

    private void OnLine(string line) => _pending.Enqueue(line);

    private void Reload()
    {
        while (_pending.TryDequeue(out _)) { }
        var lines = _log.Snapshot();
        _lines = lines.Length;
        LogBox.Text = lines.Length == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        LogBox.ScrollToEnd();
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;

        // Keep the text box bounded during long sessions.
        if (_lines > AppLog.HistoryLimit * 2)
        {
            Reload();
            return;
        }

        var text = new StringBuilder();
        while (_pending.TryDequeue(out var line))
        {
            text.AppendLine(line);
            _lines++;
        }

        LogBox.AppendText(text.ToString());
        if (FollowBox.IsChecked == true) LogBox.ScrollToEnd();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, _log.Snapshot())); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.T("Log_CopyFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_log.Directory == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.Directory}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.T("Log_FolderFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
