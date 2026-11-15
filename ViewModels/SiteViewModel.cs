using System.Globalization;
using System.Windows;
using RadioDeck.Models;
using RadioDeck.Mvvm;
using RadioDeck.Services;
using RadioDeck.Services.Localization;

namespace RadioDeck.ViewModels;

/// <summary>One radio on the main window: its calls, PTT state, levels and frequency.</summary>
public sealed class SiteViewModel : ObservableObject, IDisposable
{
    public const string NoFrequency = "---.---";

    private static readonly int[] RetryDelaysSeconds = { 2, 4, 8, 15 };
    private const int FrequencyPollSeconds = 20;
    private const int SignalLevel = 4;

    private readonly ISipRadioEngine _engine;
    private readonly TxLock _txLock;
    private readonly string _id;
    private readonly Func<AppConfig> _getConfig;
    private readonly Action _notifyGlobalStateChanged;
    private readonly Action? _persist;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connLock = new(1, 1);
    private readonly SemaphoreSlim _pttLock = new(1, 1);
    private readonly SnmpFrequencyClient _snmpClient = new();
    private readonly Timer _frequencyTimer;

    private SiteConfig _site;
    private volatile bool _disposed;

    // The operator's intent. While it is set a dropped or failed call is retried.
    private bool _wantConnected;
    private bool _connecting;
    private bool _retryPending;
    private int _retryAttempt;
    private CancellationTokenSource? _attemptCts;
    private CancellationTokenSource? _retryCts;
    private string? _rxError;
    private string? _txError;

    private RadioState _state = RadioState.Disconnected;
    private string _statusText = Loc.T("Status_Disconnected");
    private int _rxLevel;
    private int _txLevel;
    private bool _rxSquelch;
    private bool _txConfirmed;
    private bool _pttOn;
    private bool _mutedForTx;
    private bool _isSettingFrequency;
    private bool _isFrequencyEditing;
    private bool _frequencyVerified;
    private int _frequencyReadBusy;
    private string _frequencyInput = "";

    /// <param name="site">This radio's entry in the configuration; runtime changes are written back to it.</param>
    /// <param name="persist">Called after a value the operator changed on the main window must be saved.</param>
    public SiteViewModel(SiteConfig site,
        ISipRadioEngine engine,
        TxLock txLock,
        Func<AppConfig> getConfig,
        Action notifyGlobalStateChanged,
        Action? persist = null,
        Action<string>? log = null)
    {
        _site = site ?? throw new ArgumentNullException(nameof(site));
        _id = site.Id;
        _engine = engine;
        _txLock = txLock;
        _getConfig = getConfig;
        _notifyGlobalStateChanged = notifyGlobalStateChanged;
        _persist = persist;
        _log = log;

        _engine.RxLevelChanged += lvl => RunOnUi(() => RxLevel = lvl);
        _engine.TxLevelChanged += lvl => RunOnUi(() => TxLevel = lvl);
        _engine.RxSquelchChanged += open => RunOnUi(() => RxSquelch = open);
        _engine.TxConfirmedChanged += keyed => RunOnUi(() => TxConfirmed = keyed);
        _engine.RxCallEnded += () => RunOnUi(() => OnCallEnded(isTx: false));
        _engine.TxCallEnded += () => RunOnUi(() => OnCallEnded(isTx: true));

        ConnectCommand = new RelayCommand(() => _ = ConnectAsync());
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync());
        ToggleConnectCommand = new RelayCommand(() => _ = IsConnectWanted || AnyConnected ? DisconnectAsync() : ConnectAsync());
        SetFrequencyCommand = new RelayCommand(() => _ = SetFrequencyAsync(), () => CanSetFrequency);
        BeginFrequencyEditCommand = new RelayCommand(BeginFrequencyEdit, () => !IsSettingFrequency);
        CancelFrequencyEditCommand = new RelayCommand(CancelFrequencyEdit, () => IsFrequencyEditing && !IsSettingFrequency);

        _frequencyInput = _site.Frequency?.Trim() ?? "";
        ApplyVolume();
        ApplyMute();

        _frequencyTimer = new Timer(_ =>
        {
            if (AnyConnected) _ = RefreshFrequencyFromRadioAsync(quiet: true);
        }, null, TimeSpan.FromSeconds(FrequencyPollSeconds), TimeSpan.FromSeconds(FrequencyPollSeconds));
        _ = RefreshFrequencyFromRadioAsync(quiet: true);
    }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand ToggleConnectCommand { get; }
    public RelayCommand SetFrequencyCommand { get; }
    public RelayCommand BeginFrequencyEditCommand { get; }
    public RelayCommand CancelFrequencyEditCommand { get; }

    public SiteConfig Site => _site;
    public string Id => _id;
    public string Name => string.IsNullOrWhiteSpace(_site.Name) ? _id : _site.Name.Trim();

    /// <summary>false for a receive-only radio: no transmit meter, KEY switch or PTT button.</summary>
    public bool HasTx => _site.UsesTxLeg();

    public string RxUri => BuildSipUri(_site.RxUser, _site.EffectiveRxHost, _site.SipPort);
    public string TxUri => BuildSipUri(_site.TxUser, _site.EffectiveTxHost, _site.SipPort);

    public string EndpointSummary
    {
        get
        {
            var parts = new List<string> { _site.GetProfile().DisplayName };
            if (_site.UsesRxLeg())
                parts.Add($"RX {NormalizeHost(_site.EffectiveRxHost)}:{_site.SipPort} ({FormatUser(_site.RxUser)})");
            if (_site.UsesTxLeg())
                parts.Add($"{(_site.UsesRxLeg() ? "TX" : "TRX")} {NormalizeHost(_site.EffectiveTxHost)}:{_site.SipPort} ({FormatUser(_site.TxUser)})");
            return string.Join("\n", parts);
        }
    }

    // ---------------------------------------------------------------- connection state

    public RadioState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>One line for the operator: what is connected and, if not, why.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool RxConnected => _engine.IsRxConnected;
    public bool TxConnected => _engine.IsTxConnected;
    public bool AnyConnected => _engine.IsRxConnected || _engine.IsTxConnected;
    public bool LinkUp => AnyConnected;

    public bool IsFullyConnected
    {
        get
        {
            var needRx = _site.UsesRxLeg();
            var needTx = _site.UsesTxLeg();
            return (!needRx || _engine.IsRxConnected) && (!needTx || _engine.IsTxConnected);
        }
    }

    /// <summary>The operator asked for this radio to be connected (it may still be trying).</summary>
    public bool IsConnectWanted => _wantConnected;

    public string ConnectButtonText => Loc.T("Card_" + ConnectAction);

    /// <summary>What the connect button does now: Connect, Disconnect or Cancel (independent of the language).</summary>
    public string ConnectAction =>
        AnyConnected ? "Disconnect" : _wantConnected ? "Cancel" : "Connect";

    public bool CanPtt => _engine.CanTransmit;
    public bool IsTransmitting => _engine.IsSendingToTx;

    // ---------------------------------------------------------------- levels and indicators

    public int RxLevel
    {
        get => _rxLevel;
        private set
        {
            if (!Set(ref _rxLevel, value)) return;
            Raise(nameof(RxSignalDetected));
        }
    }

    public int TxLevel
    {
        get => _txLevel;
        private set
        {
            if (!Set(ref _txLevel, value)) return;
            Raise(nameof(TxSignalDetected));
        }
    }

    /// <summary>The receiver reports an open squelch (ED-137 SQU bit).</summary>
    public bool RxSquelch
    {
        get => _rxSquelch;
        private set
        {
            if (!Set(ref _rxSquelch, value)) return;
            Raise(nameof(RxSignalDetected));
        }
    }

    /// <summary>The transmitter reports that it is keyed. Not every radio echoes this.</summary>
    public bool TxConfirmed
    {
        get => _txConfirmed;
        private set => Set(ref _txConfirmed, value);
    }

    public bool RxSignalDetected => RxSquelch || RxLevel >= SignalLevel;
    public bool TxSignalDetected => PttOn || TxLevel >= SignalLevel;

    // ---------------------------------------------------------------- operator switches

    /// <summary>The global PTT key keys this radio.</summary>
    public bool TxSelected
    {
        get => _site.TxSelected;
        set
        {
            if (_site.TxSelected == value) return;
            _site.TxSelected = value;
            Raise();
            _persist?.Invoke();
            _notifyGlobalStateChanged();
        }
    }

    /// <summary>Received audio is played on the PC.</summary>
    public bool RxMonitor
    {
        get => _site.RxMonitor;
        set
        {
            if (_site.RxMonitor == value) return;
            _site.RxMonitor = value;
            Raise();
            ApplyMute();
            _persist?.Invoke();
        }
    }

    /// <summary>Playback volume in percent (0-200).</summary>
    public int RxVolume
    {
        get => _site.RxVolume;
        set
        {
            value = Math.Clamp(value, 0, 200);
            if (_site.RxVolume == value) return;
            _site.RxVolume = value;
            Raise();
            ApplyVolume();
            _persist?.Invoke();
        }
    }

    /// <summary>Called by the main view model while any radio transmits and "mute RX while TX" is on.</summary>
    public void SetRxMuted(bool muted)
    {
        _mutedForTx = muted;
        ApplyMute();
    }

    private void ApplyMute() => _engine.SetRxMuted(_mutedForTx || !_site.RxMonitor);

    private void ApplyVolume() => _engine.SetRxVolume(_site.RxVolume, _site.RxPan);

    // ---------------------------------------------------------------- PTT

    public bool PttOn
    {
        get => Volatile.Read(ref _pttOn);
        private set
        {
            if (_pttOn == value) return;
            Volatile.Write(ref _pttOn, value);
            RunOnUi(() =>
            {
                Raise(nameof(PttOn));
                Raise(nameof(PttButtonText));
                Raise(nameof(TxSignalDetected));
            });
        }
    }

    public string PttButtonText => PttOn ? Loc.T("Card_Transmitting") : "PTT";

    /// <summary>Called from the PTT controller. true starts transmitting, false stops.</summary>
    public async Task SetPttAsync(bool pressed)
    {
        await _pttLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed && pressed) return;
            if (pressed == PttOn)
                return;

            if (pressed)
                await EnsurePttOnCoreAsync().ConfigureAwait(false);
            else
                await EnsurePttOffCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _pttLock.Release();
            _notifyGlobalStateChanged();
        }
    }

    public Task EnsurePttOffAsync() => SetPttAsync(false);

    private async Task EnsurePttOnCoreAsync()
    {
        if (PttOn)
            return;

        if (!_engine.CanTransmit)
        {
            _log?.Invoke("PTT ignored: the transmitter is not connected or does not accept audio.");
            return;
        }

        var cfg = _getConfig();

        if (cfg.Ptt.SingleTxAtATime && !_txLock.TryAcquire(_id))
        {
            _log?.Invoke($"PTT ignored: another radio is transmitting ({_txLock.Owner}).");
            return;
        }

        try
        {
            await _engine.StartTxAudioAsync(_cts.Token, cfg.Audio).ConfigureAwait(false);
            PttOn = _engine.IsSendingToTx;
            if (!PttOn) _txLock.Release(_id);
        }
        catch
        {
            try { await _engine.StopTxAudioAsync(CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                PttOn = false;
                _txLock.Release(_id);
            }
            throw;
        }
    }

    private async Task EnsurePttOffCoreAsync()
    {
        try
        {
            await _engine.StopTxAudioAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            PttOn = false;
            _txLock.Release(_id);
        }
    }

    // ---------------------------------------------------------------- connect / disconnect

    /// <summary>Connects the sessions this radio needs and keeps them connected.</summary>
    public Task ConnectAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _wantConnected = true;
        _retryAttempt = 0;
        return RunConnectAsync();
    }

    private async Task RunConnectAsync()
    {
        CancelRetry();
        await _connLock.WaitAsync();
        CancellationTokenSource? attempt = null;
        try
        {
            if (!_wantConnected || _disposed) return;

            var site = _site;
            attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _attemptCts = attempt;
            _connecting = true;
            UpdateState();

            // The receiver and the transmitter are separate devices; call them together.
            var rx = ConnectLegAsync(isTx: false, site, attempt.Token);
            var tx = ConnectLegAsync(isTx: true, site, attempt.Token);
            await Task.WhenAll(rx, tx);
            _rxError = rx.Result;
            _txError = tx.Result;
        }
        catch (OperationCanceledException)
        {
            // Disconnect or shutdown during the attempt.
        }
        catch (Exception ex)
        {
            _txError = ex.Message;
            _log?.Invoke($"Connect failed: {ex}");
        }
        finally
        {
            _connecting = false;
            if (ReferenceEquals(_attemptCts, attempt)) _attemptCts = null;
            attempt?.Dispose();
            _connLock.Release();
        }

        UpdateState();
        if (_disposed || !_wantConnected) return;

        if (IsFullyConnected)
        {
            _retryAttempt = 0;
            _ = RefreshFrequencyFromRadioAsync(quiet: true);
        }
        else if (_getConfig().Connection.AutoReconnect)
        {
            ScheduleRetry();
        }
        else if (!AnyConnected)
        {
            _wantConnected = false;
            UpdateState();
        }
    }

    /// <returns>null when the leg is connected or not used; otherwise the reason it is not.</returns>
    private async Task<string?> ConnectLegAsync(bool isTx, SiteConfig site, CancellationToken ct)
    {
        if (isTx ? !site.UsesTxLeg() : !site.UsesRxLeg()) return null;
        if (isTx ? _engine.IsTxConnected : _engine.IsRxConnected) return null;

        var leg = isTx ? "TX" : "RX";
        if (NormalizeHost(isTx ? site.EffectiveTxHost : site.EffectiveRxHost).Length == 0)
            return Loc.T("Status_NoAddress", leg);

        try
        {
            if (isTx) await _engine.ConnectTxAsync(TxUri, site, ct);
            else await _engine.ConnectRxAsync(RxUri, site, ct);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RadioCallException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return $"{leg}: {ex.Message}";
        }
    }

    private void OnCallEnded(bool isTx)
    {
        // A queued old hangup must not undo a reconnect.
        if (isTx ? _engine.IsTxConnected : _engine.IsRxConnected) return;

        if (isTx)
        {
            TxLevel = 0;
            _txError = Loc.T("Status_CallEnded", "TX");
            _ = EnsurePttOffQuietAsync();
        }
        else
        {
            RxLevel = 0;
            _rxError = Loc.T("Status_CallEnded", "RX");
        }

        UpdateState();
        if (_wantConnected && !_disposed && !_connecting && _getConfig().Connection.AutoReconnect)
        {
            _retryAttempt = 0;
            ScheduleRetry();
        }
    }

    private async Task EnsurePttOffQuietAsync()
    {
        try { await EnsurePttOffAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"PTT release failed: {ex.Message}"); }
    }

    private void ScheduleRetry()
    {
        CancelRetry();
        var seconds = RetryDelaysSeconds[Math.Min(_retryAttempt, RetryDelaysSeconds.Length - 1)];
        _retryAttempt++;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _retryCts = cts;
        _retryPending = true;
        UpdateState();
        _ = RetryLaterAsync(seconds, cts);
    }

    private async Task RetryLaterAsync(int seconds, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _retryCts, null, cts) != cts) return;
        cts.Dispose();
        _retryPending = false;
        await RunConnectAsync();
    }

    private void CancelRetry()
    {
        _retryPending = false;
        var cts = Interlocked.Exchange(ref _retryCts, null);
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    public async Task DisconnectAsync()
    {
        _wantConnected = false;
        CancelRetry();
        try { _attemptCts?.Cancel(); } catch (ObjectDisposedException) { }
        UpdateState();

        await _connLock.WaitAsync();
        try
        {
            await EnsurePttOffQuietAsync();
            await _engine.DisconnectRxAsync(CancellationToken.None);
            await _engine.DisconnectTxAsync(CancellationToken.None);
            _rxError = null;
            _txError = null;
        }
        catch (Exception ex)
        {
            _txError = ex.Message;
            _log?.Invoke($"Disconnect failed: {ex.Message}");
        }
        finally
        {
            _connLock.Release();
        }

        RxLevel = 0;
        TxLevel = 0;
        UpdateState();
    }

    public async Task ReconnectAsync()
    {
        await DisconnectAsync();
        await ConnectAsync();
    }

    private void UpdateState()
    {
        if (!HasUiAccess())
        {
            RunOnUi(UpdateState);
            return;
        }

        var rx = _engine.IsRxConnected;
        var tx = _engine.IsTxConnected;
        var any = rx || tx;
        var errors = string.Join(" · ", new[] { rx ? null : _rxError, tx ? null : _txError }
            .Where(static e => !string.IsNullOrEmpty(e)));
        var retry = _retryPending ? " — " + Loc.T("Status_Retrying") : "";

        if (_connecting)
        {
            State = RadioState.Connecting;
            StatusText = Loc.T("Status_Connecting");
        }
        else if (any && IsFullyConnected)
        {
            State = RadioState.Connected;
            StatusText = Loc.T("Status_Connected");
        }
        else if (any)
        {
            State = RadioState.Partial;
            StatusText = Loc.T(rx ? "Status_RxOnly" : "Status_TxOnly") + (errors.Length > 0 ? " — " + errors : "") + retry;
        }
        else if (errors.Length > 0)
        {
            State = RadioState.Error;
            StatusText = errors + retry;
        }
        else if (_wantConnected)
        {
            State = RadioState.Connecting;
            StatusText = Loc.T("Status_NotConnected") + retry;
        }
        else
        {
            State = RadioState.Disconnected;
            StatusText = Loc.T("Status_Disconnected");
        }

        Raise(nameof(RxConnected));
        Raise(nameof(TxConnected));
        Raise(nameof(AnyConnected));
        Raise(nameof(LinkUp));
        Raise(nameof(IsFullyConnected));
        Raise(nameof(IsConnectWanted));
        Raise(nameof(ConnectButtonText));
        Raise(nameof(ConnectAction));
        Raise(nameof(CanPtt));

        _notifyGlobalStateChanged();
    }

    /// <summary>Called after the language of the user interface changed.</summary>
    public void RefreshTexts()
    {
        Raise(nameof(PttButtonText));
        Raise(nameof(EndpointSummary));
        Raise(nameof(FrequencyHint));
        UpdateState();
    }

    // ---------------------------------------------------------------- configuration

    /// <summary>Applies an edited configuration entry; the calls are re-established only if needed.</summary>
    public void UpdateConfig(SiteConfig site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var old = _site;
        _site = site;

        var sameRadio = !SiteConfig.ConnectionDiffers(old, site);
        if (!sameRadio) FrequencyVerified = false;

        IsFrequencyEditing = false;
        FrequencyInput = site.Frequency?.Trim() ?? "";
        Raise(nameof(Site));
        Raise(nameof(Name));
        Raise(nameof(HasTx));
        Raise(nameof(RxUri));
        Raise(nameof(TxUri));
        Raise(nameof(EndpointSummary));
        Raise(nameof(TxSelected));
        Raise(nameof(RxMonitor));
        Raise(nameof(RxVolume));
        RaiseFrequency();
        ApplyVolume();
        ApplyMute();

        if (!sameRadio && (_wantConnected || AnyConnected))
            _ = ReconnectAsync();
        else
            UpdateState();

        _ = RefreshFrequencyFromRadioAsync(quiet: true);
    }

    // ---------------------------------------------------------------- frequency

    /// <summary>The radio's frequency can be read and set over SNMP.</summary>
    public bool HasFrequencyControl => _site.HasFrequencyControl() && SnmpTarget.For(_site).Endpoints.Count > 0;

    public string FrequencyDisplay =>
        string.IsNullOrWhiteSpace(_site.Frequency) ? NoFrequency : _site.Frequency.Trim();

    /// <summary>The displayed value was confirmed by the radio in this session.</summary>
    public bool FrequencyVerified
    {
        get => _frequencyVerified;
        private set
        {
            if (!Set(ref _frequencyVerified, value)) return;
            Raise(nameof(FrequencyStale));
            Raise(nameof(FrequencyHint));
        }
    }

    /// <summary>A stored value the radio has not confirmed yet; shown dimmed.</summary>
    public bool FrequencyStale =>
        HasFrequencyControl && !FrequencyVerified && !string.IsNullOrWhiteSpace(_site.Frequency);

    public string FrequencyHint =>
        Loc.T(!HasFrequencyControl ? "Freq_HintLabel"
            : FrequencyVerified ? "Freq_HintRead"
            : string.IsNullOrWhiteSpace(_site.Frequency) ? "Freq_HintUnknown"
            : "Freq_HintStale");

    public string FrequencyInput
    {
        get => _frequencyInput;
        set
        {
            if (!Set(ref _frequencyInput, value)) return;
            Raise(nameof(CanSetFrequency));
            SetFrequencyCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSettingFrequency
    {
        get => _isSettingFrequency;
        private set
        {
            if (!Set(ref _isSettingFrequency, value)) return;
            RaiseFrequencyCommands();
        }
    }

    public bool IsFrequencyEditing
    {
        get => _isFrequencyEditing;
        private set
        {
            if (!Set(ref _isFrequencyEditing, value)) return;
            RaiseFrequencyCommands();
        }
    }

    public bool CanSetFrequency => IsFrequencyEditing && !IsSettingFrequency;

    private void RaiseFrequencyCommands()
    {
        Raise(nameof(CanSetFrequency));
        SetFrequencyCommand.RaiseCanExecuteChanged();
        BeginFrequencyEditCommand.RaiseCanExecuteChanged();
        CancelFrequencyEditCommand.RaiseCanExecuteChanged();
    }

    private void RaiseFrequency()
    {
        Raise(nameof(FrequencyDisplay));
        Raise(nameof(FrequencyStale));
        Raise(nameof(FrequencyHint));
        Raise(nameof(HasFrequencyControl));
    }

    public void BeginFrequencyEdit()
    {
        if (IsSettingFrequency) return;
        FrequencyInput = string.IsNullOrWhiteSpace(_site.Frequency) ? "" : _site.Frequency.Trim();
        IsFrequencyEditing = true;
    }

    public void CancelFrequencyEdit()
    {
        if (IsSettingFrequency) return;
        IsFrequencyEditing = false;
    }

    public async Task SetFrequencyAsync()
    {
        if (IsSettingFrequency)
            return;

        var site = _site;
        var text = (FrequencyInput ?? "").Trim();

        // Without SNMP the value is only the label shown on the card.
        if (!HasFrequencyControl && text.Length == 0)
        {
            StoreFrequency(site, "", verified: false);
            return;
        }

        if (!TryParseFrequencyKhz(text, out var khz, out var normalized, out var error))
        {
            StatusText = Loc.T("Freq_NotChanged", error);
            return;
        }

        if (!HasFrequencyControl)
        {
            StoreFrequency(site, normalized, verified: false);
            return;
        }

        IsSettingFrequency = true;
        try
        {
            var results = await _snmpClient.SetFrequencyAsync(SnmpTarget.For(site), khz, _cts.Token);
            var failures = results.Where(static r => !r.Success).ToList();
            if (failures.Count > 0)
            {
                var message = string.Join(" · ", failures.Select(f => $"{f.Host}: {f.Message}"));
                StatusText = Loc.T("Freq_NotChanged", message);
                _log?.Invoke($"Frequency set to {normalized} failed: {message}");
                // A pair may now be split across two frequencies; show what the radio really has.
                if (failures.Count < results.Count) _ = RefreshFrequencyFromRadioAsync(quiet: true);
                return;
            }

            StoreFrequency(site, normalized, verified: true);
            _log?.Invoke($"Frequency set to {normalized} on {string.Join(", ", results.Select(r => r.Host))}.");
            UpdateState();
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Freq_NotChanged", ex.Message);
        }
        finally
        {
            IsSettingFrequency = false;
        }
    }

    private void StoreFrequency(SiteConfig site, string value, bool verified)
    {
        var changed = !string.Equals(site.Frequency, value, StringComparison.Ordinal);
        site.Frequency = value;
        if (!ReferenceEquals(site, _site)) return;
        FrequencyVerified = verified;
        IsFrequencyEditing = false;
        RaiseFrequency();
        if (changed) _persist?.Invoke();
    }

    public async Task RefreshFrequencyFromRadioAsync(bool quiet)
    {
        if (_disposed || IsSettingFrequency) return;
        var site = _site;
        if (!site.HasFrequencyControl()) return;
        var target = SnmpTarget.For(site);
        if (target.Endpoints.Count == 0) return;
        if (Interlocked.Exchange(ref _frequencyReadBusy, 1) != 0) return;

        try
        {
            var result = await _snmpClient.GetFrequencyAsync(target, _cts.Token).ConfigureAwait(false);
            if (_disposed || !ReferenceEquals(site, _site)) return;

            if (!result.Success)
            {
                if (!quiet) _log?.Invoke($"Frequency read failed: {result.Message}");
                return;
            }

            var normalized = (result.FrequencyKhz / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
            RunOnUi(() =>
            {
                if (!ReferenceEquals(site, _site) || IsSettingFrequency) return;
                var changed = !string.Equals(site.Frequency, normalized, StringComparison.Ordinal);
                site.Frequency = normalized;
                FrequencyVerified = true;
                RaiseFrequency();
                if (changed)
                {
                    _log?.Invoke($"Frequency read from {result.Host}: {normalized}");
                    _persist?.Invoke();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            if (!quiet) _log?.Invoke($"Frequency read failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _frequencyReadBusy, 0);
        }
    }

    /// <summary>
    /// Accepts "123.450", "123,45", "123450" (kHz) or "123" (MHz) in the VHF (108-156 MHz)
    /// and UHF (225-400 MHz) aeronautical bands.
    /// </summary>
    public static bool TryParseFrequencyKhz(string? input, out int khz, out string normalized, out string error)
    {
        khz = 0;
        normalized = "";
        error = string.Empty;

        var text = (input ?? string.Empty).Trim().Replace(',', '.');
        if (text.Length == 0)
        {
            error = Loc.T("Freq_ErrEmpty");
            return false;
        }

        if (text.Contains('.'))
        {
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz) ||
                mhz is < 0 or > 1000)
            {
                error = Loc.T("Freq_ErrNumber");
                return false;
            }

            khz = (int)Math.Round(mhz * 1000.0, MidpointRounding.AwayFromZero);
        }
        else
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out khz))
            {
                error = Loc.T("Freq_ErrNumber");
                return false;
            }

            if (khz < 1000)
                khz *= 1000;
        }

        var vhf = khz is >= 108_000 and <= 156_000;
        var uhf = khz is >= 225_000 and <= 400_000;
        if (!vhf && !uhf)
        {
            khz = 0;
            error = Loc.T("Freq_ErrRange");
            return false;
        }

        normalized = (khz / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
        return true;
    }

    // ---------------------------------------------------------------- helpers

    private static bool HasUiAccess()
        => Application.Current?.Dispatcher?.CheckAccess() ?? true;

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

    private static string BuildSipUri(string? user, string host, int port)
    {
        var cleanHost = NormalizeHost(host);
        var cleanUser = NormalizeUser(user);
        if (string.IsNullOrWhiteSpace(cleanHost))
            cleanHost = "127.0.0.1";

        if (string.IsNullOrWhiteSpace(cleanUser))
            return $"sip:{cleanHost}:{port}";

        return $"sip:{cleanUser}@{cleanHost}:{port}";
    }

    internal static string NormalizeHost(string? host)
    {
        var value = host?.Trim() ?? string.Empty;
        if (value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        var atIdx = value.LastIndexOf('@');
        if (atIdx >= 0 && atIdx < value.Length - 1)
            value = value[(atIdx + 1)..];

        var colonIdx = value.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < value.Length - 1 && int.TryParse(value[(colonIdx + 1)..], out _))
            value = value[..colonIdx];

        return value.Trim();
    }

    private static string NormalizeUser(string? user)
    {
        var value = user?.Trim() ?? string.Empty;
        if (value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        var atIdx = value.IndexOf('@');
        if (atIdx > 0)
            value = value[..atIdx];

        return value.Trim().TrimEnd('@');
    }

    private static string FormatUser(string? user)
        => string.IsNullOrWhiteSpace(user) ? "-" : user.Trim();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wantConnected = false;
        _frequencyTimer.Dispose();
        CancelRetry();
        _cts.Cancel();
        _pttLock.Wait();
        try
        {
            _engine.Dispose();
        }
        finally
        {
            PttOn = false;
            _txLock.Release(_id);
            _pttLock.Release();
        }
    }
}
