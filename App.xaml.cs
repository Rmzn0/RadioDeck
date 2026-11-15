using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadioDeck.Models;
using RadioDeck.Services.Localization;
using RadioDeck.ViewModels;
using RadioDeck.Views;

namespace RadioDeck;

/// <summary>
/// Command line:
///   (none)              normal operation
///   --demo              simulated radios; no network, audio device, key hook or config file
///   --render-ui DIR     write PNG pictures of the demo windows to DIR and exit (used for the README)
///   --lang CODE         language of the demo and of the pictures, e.g. "de"; normal operation
///                       takes the language from the settings
/// </summary>
public partial class App : Application
{
    private const string MainWindowTitle = "RadioDeck";

    private Mutex? _singleInstance;
    private MainViewModel? _viewModel;
    private bool _timerResolutionRaised;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportCrash("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var languageIndex = Array.FindIndex(e.Args, a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase));
        var language = languageIndex >= 0 && languageIndex + 1 < e.Args.Length ? e.Args[languageIndex + 1] : null;

        var renderIndex = Array.FindIndex(e.Args, a => a.Equals("--render-ui", StringComparison.OrdinalIgnoreCase));
        if (renderIndex >= 0)
        {
            // The pictures are English unless a language is asked for.
            if (language != null) Loc.Use(language);
            var directory = renderIndex + 1 < e.Args.Length ? e.Args[renderIndex + 1] : "ui-render";
            Shutdown(RenderUi(Path.GetFullPath(directory), language));
            return;
        }

        var demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        if (!demo && !AcquireSingleInstance())
        {
            // Two copies would fight over the PTT key and call each radio twice.
            ActivateRunningInstance();
            Shutdown(0);
            return;
        }

        // 20 ms audio pacing needs a finer timer than the default 15.6 ms Windows tick.
        _timerResolutionRaised = timeBeginPeriod(1) == 0;

        if (demo)
        {
            Loc.Use(language);
            var config = CreateDemoConfig(8, grid: true);
            config.Ui.Language = language == null ? "" : Loc.Language.Code;
            _viewModel = new MainViewModel(config);
        }
        else
        {
            _viewModel = new MainViewModel();
        }
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_timerResolutionRaised) timeEndPeriod(1);
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstance = new Mutex(initiallyOwned: true, @"Local\RadioDeck.SingleInstance", out var created);
            if (created) return true;
            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return true; // never block the radio console because of a mutex problem
        }
    }

    private static void ActivateRunningInstance()
    {
        var handle = FindWindow(null, MainWindowTitle);
        if (handle == IntPtr.Zero) return;
        if (IsIconic(handle)) ShowWindow(handle, 9 /* SW_RESTORE */);
        SetForegroundWindow(handle);
    }

    // ---------------------------------------------------------------- error reporting

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the calls up: a fault in the user interface must not take the radios down.
        e.Handled = true;
        ReportCrash("User interface error", e.Exception);
        try
        {
            MessageBox.Show(
                Loc.T("Main_UnexpectedError", e.Exception.Message),
                MainWindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // no message box possible; the log entry remains
        }
    }

    private void ReportCrash(string kind, Exception? exception)
    {
        var text = $"{kind}: {exception}";
        try { _viewModel?.Log.Write(text); } catch { }
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioDeck", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch
        {
            // nothing else can be done
        }
    }

    // ---------------------------------------------------------------- demo data and pictures

    private static AppConfig CreateDemoConfig(int radios, bool grid)
    {
        var samples = new (string Name, string Frequency, string Profile, RadioLinkMode Mode)[]
        {
            ("LOCALIZER", "110.300", RadioProfile.BasicId, RadioLinkMode.Separate),
            ("GLIDEPATH", "335.000", RadioProfile.BasicId, RadioLinkMode.Separate),
            ("TOWER", "118.100", RadioProfile.BasicId, RadioLinkMode.Separate),
            ("GROUND", "121.900", RadioProfile.Ed137BId, RadioLinkMode.Transceiver),
            ("APPROACH", "120.500", RadioProfile.Ed137CId, RadioLinkMode.Transceiver),
            ("ATIS", "128.200", RadioProfile.Ed137BId, RadioLinkMode.RxOnly),
            ("EMERGENCY", "121.500", RadioProfile.BasicId, RadioLinkMode.Separate),
            ("COMPANY", "131.750", RadioProfile.Ed137CId, RadioLinkMode.Transceiver)
        };

        var config = new AppConfig { ConfigVersion = AppConfig.CurrentVersion };
        for (var i = 0; i < Math.Clamp(radios, 1, samples.Length); i++)
        {
            var sample = samples[i];
            config.Radios.Add(new SiteConfig
            {
                Id = $"DEMO{i + 1}",
                Name = sample.Name,
                Profile = sample.Profile,
                LinkMode = sample.Mode.ToString(),
                // Documentation addresses (RFC 5737); the demo never contacts them.
                RxHost = sample.Mode == RadioLinkMode.Transceiver ? "" : $"192.0.2.{10 + i * 2}",
                TxHost = sample.Mode == RadioLinkMode.RxOnly ? "" : $"192.0.2.{11 + i * 2}",
                RxUser = $"RX_{i + 1:00}",
                TxUser = $"TX_{i + 1:00}",
                Frequency = sample.Frequency,
                SnmpEnabled = false,
                TxSelected = i == 0
            });
        }
        config.Ui.Mode = grid ? UiConfig.Grid : UiConfig.Classic;
        config.Ptt.PttKey = "F9";
        config.Audio.NoiseGateConfigured = true;
        return config;
    }

    private static int RenderUi(string directory, string? language)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var bindingErrors = new StreamWriter(Path.Combine(directory, "binding-errors.txt"), append: false);
            var listener = new TextWriterTraceListener(bindingErrors);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            foreach (var (name, radios, grid) in new[]
                     {
                         ("classic-2", 2, false), ("classic-4", 4, false),
                         ("grid-3", 3, true), ("grid-5", 5, true), ("grid-8", 8, true)
                     })
            {
                using var vm = new MainViewModel(CreateDemoConfig(radios, grid));
                var window = new MainWindow(vm);
                Layout((FrameworkElement)window.Content, null);

                // Show a realistic moment: all but the last radio connected, one transmitting.
                for (var i = 0; i < vm.Radios.Count; i++)
                    if (i == 0 || i < vm.Radios.Count - 1) _ = vm.Radios[i].ConnectAsync();
                Pump(900);
                if (vm.Radios.Count > 2) vm.SetMousePtt(vm.Radios[2]);
                Pump(700);

                Save((FrameworkElement)window.Content, Path.Combine(directory, $"main-{name}.png"), null);
                vm.SetMousePtt(null);
                Pump(100);

                // Layout check for translators: the frequency editor with its buttons.
                if (language != null && radios <= 3)
                {
                    vm.Radios[0].BeginFrequencyEdit();
                    Pump(200);
                    Save((FrameworkElement)window.Content, Path.Combine(directory, $"main-{name}-edit.png"), null);
                    vm.Radios[0].CancelFrequencyEdit();
                }
            }

            var settings = new SettingsWindow { DataContext = new SettingsViewModel(CreateDemoConfig(5, grid: true)) { ConfigPath = @"%AppData%\RadioDeck\config.json" } };
            var content = (FrameworkElement)settings.Content;
            var tabs = FindChild<TabControl>(content);
            var names = new[] { "radios", "ptt", "audio", "general" };
            for (var i = 0; i < names.Length; i++)
            {
                if (tabs != null) tabs.SelectedIndex = i;
                Pump(150);
                Save(content, Path.Combine(directory, $"settings-{names[i]}.png"), new Size(settings.Width - 16, settings.Height - 39));
                if (i == 0 && FindChild<Expander>(content) is { } advanced)
                {
                    advanced.IsExpanded = true;
                    Pump(600);
                    DependencyObject? parent = advanced;
                    while (parent != null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
                    (parent as ScrollViewer)?.ScrollToEnd();
                    Pump(300);
                    Save(content, Path.Combine(directory, "settings-advanced.png"), new Size(settings.Width - 16, settings.Height - 39));
                    advanced.IsExpanded = false;
                    (parent as ScrollViewer)?.ScrollToTop();
                    Pump(300);
                }
            }

            // The log window with the lines of a short demo session, and the PIN prompt.
            using (var vm = new MainViewModel(CreateDemoConfig(2, grid: false)))
            {
                foreach (var radio in vm.Radios) _ = radio.ConnectAsync();
                Pump(900);
                vm.SetMousePtt(vm.Radios[0]);
                Pump(500);
                vm.SetMousePtt(null);
                Pump(300);
                var log = new LogWindow(vm.Log);
                Save((FrameworkElement)log.Content, Path.Combine(directory, "log.png"), new Size(820, 380));
                log.Close();
            }

            var pin = new PinDialog(_ => false, 3);
            var prompt = (FrameworkElement)pin.Content;
            prompt.Width = pin.Width - 16;
            Save(prompt, Path.Combine(directory, "pin.png"), null);
            pin.Close();

            listener.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(directory, "render-error.txt"), ex.ToString()); } catch { }
            return 1;
        }
    }

    // Lays the element out without showing a window and writes it as PNG.
    private static void Save(FrameworkElement element, string path, Size? size)
    {
        var final = Layout(element, size);

        const int margin = 10;
        var width = (int)Math.Ceiling(final.Width) + margin * 2;
        var height = (int)Math.Ceiling(final.Height) + margin * 2;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), null, new Rect(0, 0, width, height));
            // Pin the brush to the layout rectangle: by default it uses the bounds of everything
            // drawn (glows and overhanging glyphs included) and centres that in the target.
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, final.Width, final.Height)
            };
            context.DrawRectangle(brush, null, new Rect(margin, margin, final.Width, final.Height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // Bindings and data triggers resolve during the first pass and can change the template,
    // so repeat until the wanted size no longer changes.
    private static Size Layout(FrameworkElement element, Size? size)
    {
        var available = size ?? new Size(double.PositiveInfinity, double.PositiveInfinity);
        var final = new Size();
        for (var pass = 0; pass < 4; pass++)
        {
            element.InvalidateMeasure();
            element.Measure(available);
            var wanted = size ?? element.DesiredSize;
            element.Arrange(new Rect(wanted));
            element.UpdateLayout();
            Pump(30);
            if (wanted == final) break;
            final = wanted;
        }
        return final;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
}
