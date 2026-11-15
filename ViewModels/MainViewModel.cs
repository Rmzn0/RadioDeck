using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadioDeck.Models;
using RadioDeck.Mvvm;
using RadioDeck.Services;
using RadioDeck.Services.Audio;
using RadioDeck.Services.Localization;
using RadioDeck.Views;

namespace RadioDeck.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int PinAttempts = 3;
    private static readonly TimeSpan PinLockout = TimeSpan.FromSeconds(30);

    private readonly TxLock _txLock = new();
    private readonly ConfigStore? _store; // null in demo mode: nothing is read from or written to disk
    private readonly GlobalKeyboardHook _globalHook = new();
    private readonly MicrophoneHub _microphone = new();
    private readonly PttController _ptt;
    private readonly DispatcherTimer _saveTimer;
    private volatile SiteViewModel[] _slots = Array.Empty<SiteViewModel>();
    private AppConfig _config;
    private Key _pttKey;
    private int _keyboardTargets = -1;
    private bool _keyHeld;
    private bool _mouseHeld;
    private bool _savePending;
    private bool _disposed;
    private bool _keyHookFailed;
    private DateTime _pinLockedUntil;
    private SettingsWindow? _settingsWindow;
    private LogWindow? _logWindow;

    private string _notice = "";
    private bool _transmitLimitReached;

    /// <summary>The radios in display order.</summary>
    public ObservableCollection<SiteViewModel> Radios { get; } = new();

    public AppLog Log { get; }

    /// <summary>Simulated radios; no network, audio device, keyboard hook or config file is used.</summary>
    public bool IsDemo { get; }

    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand ToggleViewCommand { get; }
    public RelayCommand ToggleConnectAllCommand { get; }

    public MainViewModel() : this(null)
    {
    }

    /// <param name="demoConfig">When given, the program runs in demo mode with this configuration.</param>
    public MainViewModel(AppConfig? demoConfig)
    {
        IsDemo = demoConfig != null;
        FooterLogo = IsDemo ? null : LoadFooterLogo();
        if (demoConfig != null)
        {
            _config = demoConfig;
            ConfigMigrations.Normalize(_config);
            Log = new AppLog(null);
            Log.Write("Demo mode: simulated radios, nothing is sent to the network.");
        }
        else
        {
            // A warning about an unreadable file is worded before the stored language is known.
            Loc.Use(null);
            _store = new ConfigStore(appName: "RadioDeck");
            Log = new AppLog(Path.Combine(_store.DirectoryOnDisk, "logs"));
            _config = LoadConfigSafe();
            Loc.Use(_config.Ui.Language);
            Log.Write($"RadioDeck {VersionText} started. Config: {_store.PathOnDisk}. Language: {Loc.Language.Code}");
            if (_store.LoadWarning is { } warning)
            {
                _notice = warning;
                Log.Write(warning);
            }
        }

        _pttKey = ParseKeySafe(_config.Ptt.PttKey);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => SaveNow();

        _microphone.Log += Log.Write;
        _microphone.SetNoiseReduction(_config.Audio.NoiseReduction);
        _microphone.SetDevice(_config.Audio.InputDevice);

        _ptt = new PttController(AppConfig.MaxRadios, ApplyPttAsync,
            () => _config.Ptt,
            connected: () => Mask(static s => s.CanPtt),
            transmitting: () => Mask(static s => s.IsTransmitting),
            keyboardTargets: () => Mask(static s => s.TxSelected));
        _ptt.Error += ex => Log.Write($"[PTT][ERR] {ex.Message}");
        _ptt.TimedOut += () => RunOnUi(() =>
        {
            TransmitLimitReached = true;
            Log.Write($"[PTT] Transmit time limit of {_config.Ptt.MaxTxSeconds} s reached; transmission stopped.");
        });

        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenLogCommand = new RelayCommand(OpenLog);
        ToggleViewCommand = new RelayCommand(() => IsGridMode = !IsGridMode);
        ToggleConnectAllCommand = new RelayCommand(ToggleConnectAll);

        RebuildRadios();

        if (!IsDemo)
        {
            try
            {
                _globalHook.Log += Log.Write;
                _globalHook.KeyChanged += GlobalHook_KeyChanged;
                _globalHook.Start(_pttKey, _config.Ptt.SuppressKey);
            }
            catch (Exception ex)
            {
                Log.Write($"[PTT][KEY][ERR] Global key hook could not be started: {ex.Message}");
                _keyHookFailed = true;
                Notice = Loc.T("Main_KeyHookFailed");
            }
        }

        Loc.Changed += OnLanguageChanged;
        NotifyGlobalStateChanged();
    }

    private void OnLanguageChanged() => RunOnUi(() =>
    {
        if (_disposed) return;
        Raise(nameof(ViewToggleToolTip));
        Raise(nameof(ConnectAllToolTip));
        Raise(nameof(PttKeyText));
        if (_keyHookFailed && HasNotice) Notice = Loc.T("Main_KeyHookFailed");
        foreach (var radio in _slots) radio.RefreshTexts();
    });

    // ---------------------------------------------------------------- view state

    public static string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string WindowTitle => IsDemo ? "RadioDeck (demo)" : "RadioDeck";

    /// <summary>Square tiles, four per row, instead of the tall classic cards.</summary>
    public bool IsGridMode
    {
        get => _config.Ui.Mode == UiConfig.Grid;
        set
        {
            if (IsGridMode == value) return;
            _config.Ui.Mode = value ? UiConfig.Grid : UiConfig.Classic;
            Raise();
            Raise(nameof(ViewToggleToolTip));
            SaveSoon();
        }
    }

    public string ViewToggleToolTip => Loc.T(IsGridMode ? "Main_ViewToClassic" : "Main_ViewToGrid");

    /// <summary>Tiles per row in the grid view: the first four fill the first row, the rest wrap below.</summary>
    public int GridColumns => Math.Clamp(Radios.Count, 1, 4);

    public bool AlwaysOnTop => _config.Ui.AlwaysOnTop;
    public string Organization => _config.Ui.Organization ?? "";
    public bool HasOrganization => !string.IsNullOrWhiteSpace(_config.Ui.Organization);
    public string PttKeyText => IsDemo ? Loc.T("Main_PttOnScreen") : Loc.T("Main_PttKey", _pttKey);

    /// <summary>Optional organisation logo; never shown in demo mode (screenshots, tests).</summary>
    public ImageSource? FooterLogo { get; }
    public bool HasFooterLogo => FooterLogo != null;

    /// <summary>A message the operator should read, e.g. an unreadable configuration file.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (!Set(ref _notice, value)) return;
            Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    /// <summary>The transmission was cut by the time limit and PTT is still held.</summary>
    public bool TransmitLimitReached
    {
        get => _transmitLimitReached;
        private set => Set(ref _transmitLimitReached, value);
    }

    public bool AnyConnectWanted => _slots.Any(static s => s.IsConnectWanted || s.AnyConnected);
    public string ConnectAllToolTip => Loc.T(AnyConnectWanted ? "Main_DisconnectAll" : "Main_ConnectAll");

    public bool IsPttKey(Key key) => !IsDemo && key == _pttKey;

    // ---------------------------------------------------------------- radios

    private void RebuildRadios()
    {
        var existing = Radios.ToDictionary(static r => r.Id, StringComparer.OrdinalIgnoreCase);
        var next = new List<SiteViewModel>();
        foreach (var site in _config.Radios)
        {
            if (existing.Remove(site.Id, out var radio))
                radio.UpdateConfig(site);
            else
                radio = CreateRadio(site, next.Count);
            next.Add(radio);
        }

        // The PTT controller must stop addressing a radio before it is disposed.
        _slots = next.ToArray();
        foreach (var removed in existing.Values)
        {
            Log.Write($"{removed.Name}: removed.");
            removed.Dispose();
        }

        if (!Radios.SequenceEqual(next))
        {
            Radios.Clear();
            foreach (var radio in next) Radios.Add(radio);
        }

        Raise(nameof(GridColumns));
    }

    private SiteViewModel CreateRadio(SiteConfig site, int index)
    {
        ISipRadioEngine engine = IsDemo
            ? new DemoRadioEngine(index)
            : new SipsorceryRadioEngine(_microphone, () => _config.Audio);

        SiteViewModel? radio = null;
        string Source() => radio?.Name ?? site.Name;
        engine.Log += message => Log.Write(Source(), message);
        radio = new SiteViewModel(site, engine, _txLock, () => _config, NotifyGlobalStateChanged, SaveSoon,
            message => Log.Write(Source(), message));
        return radio;
    }

    private PttTargets Mask(Func<SiteViewModel, bool> predicate)
    {
        var slots = _slots;
        var mask = PttTargets.None;
        for (var i = 0; i < slots.Length; i++)
            if (predicate(slots[i])) mask |= PttController.Slot(i);
        return mask;
    }

    private void NotifyGlobalStateChanged()
    {
        UpdateRxMute();

        // Selecting or deselecting a radio takes effect immediately, also while the key is held.
        var keyboardTargets = (int)Mask(static s => s.TxSelected);
        if (Interlocked.Exchange(ref _keyboardTargets, keyboardTargets) != keyboardTargets)
            _ptt?.Refresh();

        RunOnUi(() =>
        {
            Raise(nameof(AnyConnectWanted));
            Raise(nameof(ConnectAllToolTip));
        });
    }

    private void UpdateRxMute(bool startingTx = false)
    {
        var slots = _slots;
        var muted = _config.Ptt.MuteRxWhileTx && (startingTx || slots.Any(static s => s.IsTransmitting));
        foreach (var radio in slots) radio.SetRxMuted(muted);
    }

    private async Task ApplyPttAsync(int slot, bool down)
    {
        var slots = _slots;
        if (slot >= slots.Length) return;
        var radio = slots[slot];
        if (down && radio.CanPtt) UpdateRxMute(startingTx: true);
        try { await radio.SetPttAsync(down).ConfigureAwait(false); }
        finally { UpdateRxMute(); }
    }

    private void ToggleConnectAll()
    {
        var disconnect = AnyConnectWanted;
        foreach (var radio in _slots)
            _ = disconnect ? radio.DisconnectAsync() : radio.ConnectAsync();
    }

    /// <summary>Called once the main window is on screen.</summary>
    public void OnWindowLoaded()
    {
        if (!_config.Connection.ConnectOnStartup) return;
        Log.Write("Connecting all radios (connect on startup).");
        foreach (var radio in _slots) _ = radio.ConnectAsync();
    }

    // ---------------------------------------------------------------- PTT inputs

    private void GlobalHook_KeyChanged(bool isDown)
    {
        _ptt.SetKeyboard(isDown);
        _keyHeld = isDown;
        if (!isDown) ClearLimitWhenReleased();
    }

    /// <summary>Mouse PTT: the radio whose on-screen button is held, or null when released.</summary>
    public void SetMousePtt(SiteViewModel? radio)
    {
        var index = radio == null ? -1 : Array.IndexOf(_slots, radio);
        _ptt.SetMouse(index < 0 ? PttTargets.None : PttController.Slot(index));
        _mouseHeld = index >= 0;
        if (!_mouseHeld) ClearLimitWhenReleased();
    }

    private void ClearLimitWhenReleased()
    {
        if (_keyHeld || _mouseHeld) return;
        RunOnUi(() => TransmitLimitReached = false);
    }

    // ---------------------------------------------------------------- settings

    private void OpenSettings()
    {
        try
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            // The PTT key must be free for key capture and must not transmit while typing.
            _ptt.SetSuspended(true);
            if (!ConfirmPin())
            {
                _ptt.SetSuspended(false);
                return;
            }

            var vm = new SettingsViewModel(_config) { ConfigPath = _store?.PathOnDisk ?? Loc.T("Main_DemoNotSaved") };
            var win = new SettingsWindow
            {
                DataContext = vm,
                Owner = Application.Current?.MainWindow
            };

            vm.RequestClose += result =>
            {
                try
                {
                    if (result == true)
                    {
                        ApplyConfig(vm.ToConfig());
                        Log.Write("Settings saved.");
                    }
                    win.Close();
                }
                catch (Exception ex)
                {
                    Log.Write($"Settings could not be saved: {ex}");
                    MessageBox.Show(win, ex.Message, Loc.T("Main_SettingsNotSaved"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            win.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, win))
                    _settingsWindow = null;
                _ptt.SetSuspended(false);
            };

            _settingsWindow = win;
            win.Show();
            win.Activate();
        }
        catch (Exception ex)
        {
            _ptt.SetSuspended(false);
            Log.Write($"Settings window failed: {ex}");
        }
    }

    private bool ConfirmPin()
    {
        if (!_config.Security.HasPin) return true;

        var wait = _pinLockedUntil - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            var text = Loc.T("Main_PinLocked", (int)Math.Ceiling(wait.TotalSeconds));
            if (Application.Current?.MainWindow is { } owner)
                MessageBox.Show(owner, text, Loc.T("Main_Settings"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(text, Loc.T("Main_Settings"), MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var dialog = new PinDialog(pin => PinHasher.Verify(_config.Security, pin), PinAttempts)
        {
            Owner = Application.Current?.MainWindow
        };
        var ok = dialog.ShowDialog() == true;
        if (!ok && dialog.AttemptsExhausted)
        {
            _pinLockedUntil = DateTime.UtcNow + PinLockout;
            Log.Write("Settings: wrong PIN entered three times; locked for 30 s.");
        }
        return ok;
    }

    private void ApplyConfig(AppConfig config)
    {
        ConfigMigrations.Normalize(config);
        _store?.Save(config);

        var outputChanged = !string.Equals(_config.Audio.OutputDevice, config.Audio.OutputDevice, StringComparison.OrdinalIgnoreCase);
        var languageChanged = !string.Equals(_config.Ui.Language, config.Ui.Language, StringComparison.OrdinalIgnoreCase);
        _config = config;
        _savePending = false;
        _saveTimer.Stop();

        _pttKey = ParseKeySafe(_config.Ptt.PttKey);
        if (!IsDemo) _globalHook.Configure(_pttKey, _config.Ptt.SuppressKey);
        _microphone.SetNoiseReduction(_config.Audio.NoiseReduction);
        _microphone.SetDevice(_config.Audio.InputDevice);

        var connectedBefore = _slots.Where(static s => s.AnyConnected).ToList();
        RebuildRadios();

        // Playback devices are opened per call; a new output device needs fresh calls.
        if (outputChanged)
            foreach (var radio in connectedBefore.Where(r => _slots.Contains(r) && r.AnyConnected))
                _ = radio.ReconnectAsync();

        _keyHookFailed = false;
        Notice = "";
        if (languageChanged) Loc.Use(_config.Ui.Language);
        Raise(nameof(IsGridMode));
        Raise(nameof(ViewToggleToolTip));
        Raise(nameof(AlwaysOnTop));
        Raise(nameof(Organization));
        Raise(nameof(HasOrganization));
        Raise(nameof(PttKeyText));
        NotifyGlobalStateChanged();
    }

    // Frequency, volume and the on-screen switches change often; write them once things settle.
    private void SaveSoon()
    {
        if (_store == null || _disposed) return;
        RunOnUi(() =>
        {
            _savePending = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        });
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        if (!_savePending || _store == null) return;
        _savePending = false;
        try { _store.Save(_config); }
        catch (Exception ex) { Log.Write($"Config could not be saved: {ex.Message}"); }
    }

    private AppConfig LoadConfigSafe()
    {
        try
        {
            return _store!.Load();
        }
        catch (Exception ex)
        {
            _notice = Loc.T("Main_ConfigNotLoaded", ex.Message);
            Log.Write(_notice);
            return AppConfig.CreateDefault();
        }
    }

    private static Key ParseKeySafe(string? keyName) =>
        SettingsViewModel.TryParseKey(keyName, out var key) ? key : Key.F9;

    // ---------------------------------------------------------------- log window

    private void OpenLog()
    {
        if (_logWindow is { IsVisible: true })
        {
            _logWindow.Activate();
            return;
        }

        var win = new LogWindow(Log) { Owner = Application.Current?.MainWindow };
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_logWindow, win)) _logWindow = null;
        };
        _logWindow = win;
        win.Show();
    }

    // ---------------------------------------------------------------- helpers

    // An organisation logo is optional and never built in: a logo.png / logo.jpg
    // placed next to config.json is shown in the footer.
    private static ImageSource? LoadFooterLogo()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioDeck");
            foreach (var name in new[] { "logo.png", "logo.jpg", "logo.jfif" })
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loc.Changed -= OnLanguageChanged;
        try { _globalHook.KeyChanged -= GlobalHook_KeyChanged; } catch { }
        try { _globalHook.Dispose(); } catch { }
        _ptt.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _savePending |= _saveTimer.IsEnabled;
        SaveNow();
        foreach (var radio in _slots) radio.Dispose();
        _microphone.Dispose();
        Log.Write("Program closed.");
        Log.Dispose();
    }
}
