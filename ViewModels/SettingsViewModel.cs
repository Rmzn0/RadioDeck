using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Windows.Input;
using RadioDeck.Models;
using RadioDeck.Mvvm;
using RadioDeck.Services;
using RadioDeck.Services.Audio;
using RadioDeck.Services.Localization;

namespace RadioDeck.ViewModels;

/// <summary>A stored value and the text shown for it in a list.</summary>
public sealed record Choice(string Value, string Label);
public sealed record LevelChoice(int Value, string Label);

/// <summary>Editable copy of one radio's settings.</summary>
public sealed class RadioSettingsViewModel : ObservableObject
{
    private readonly SiteConfig _source;
    private string _name;
    private string _profile;
    private string _linkMode;
    private string _rxHost;
    private string _txHost;
    private string _port;
    private string _rxUser;
    private string _txUser;
    private string _frequency;
    private int _rxVolume;
    private int _rxPan;
    private string _snmp;
    private string _snmpCommunity;
    private string _snmpPort;
    private string _snmpVersion;
    private string _snmpUnit;
    private string _snmpFrequencyOid;
    private string _snmpTxFrequencyOid;
    private string _snmpRxHost;
    private string _snmpTxHost;
    private string _snmpTestResult = "";
    private bool _isSnmpTesting;
    private string _wg67Version;
    private string _user;
    private string _pass;

    public RadioSettingsViewModel(SiteConfig source)
    {
        _source = source;
        _name = source.Name ?? "";
        _profile = source.GetProfile().Id;
        _linkMode = source.GetLinkMode().ToString();
        _rxHost = source.EffectiveRxHost ?? "";
        _txHost = source.EffectiveTxHost ?? "";
        _port = source.SipPort.ToString(CultureInfo.InvariantCulture);
        _rxUser = source.RxUser ?? "";
        _txUser = source.TxUser ?? "";
        _frequency = source.Frequency ?? "";
        _rxVolume = source.RxVolume;
        _rxPan = source.RxPan;
        _snmp = source.SnmpEnabled switch { true => "On", false => "Off", _ => "Auto" };
        _snmpCommunity = source.SnmpCommunity ?? "public";
        _snmpPort = source.SnmpPort.ToString(CultureInfo.InvariantCulture);
        _snmpVersion = SnmpTarget.NormalizeVersion(source.SnmpVersion);
        _snmpUnit = SnmpTarget.UnitName(SnmpTarget.ParseUnit(source.SnmpFrequencyUnit));
        _snmpFrequencyOid = source.SnmpFrequencyOid ?? "";
        _snmpTxFrequencyOid = source.SnmpTxFrequencyOid ?? "";
        _snmpRxHost = source.SnmpRxHost ?? "";
        _snmpTxHost = source.SnmpTxHost ?? "";
        _wg67Version = source.Wg67Version ?? "";
        _user = source.User ?? "";
        _pass = source.Pass ?? "";

        ProfileChoices = RadioProfile.All.Select(p => new Choice(p.Id, p.DisplayName)).ToList();
        LinkModeChoices = new[]
        {
            new Choice(nameof(RadioLinkMode.Separate), Loc.T("Link_Separate")),
            new Choice(nameof(RadioLinkMode.Transceiver), Loc.T("Link_Transceiver")),
            new Choice(nameof(RadioLinkMode.RxOnly), Loc.T("Link_RxOnly"))
        };
        SnmpChoices = new[]
        {
            new Choice("Auto", Loc.T("Snmp_Auto")),
            new Choice("On", Loc.T("Snmp_On")),
            new Choice("Off", Loc.T("Snmp_Off"))
        };
        SnmpVersionChoices = new[]
        {
            new Choice(SnmpTarget.Version2c, "v2c"),
            new Choice(SnmpTarget.VersionOne, "v1")
        };
        SnmpUnitChoices = new[]
        {
            new Choice(SnmpTarget.UnitKhz, Loc.T("Unit_Khz")),
            new Choice(SnmpTarget.UnitHz, Loc.T("Unit_Hz")),
            new Choice(SnmpTarget.UnitMhzText, Loc.T("Unit_MhzText"))
        };
        TestSnmpCommand = new RelayCommand(() => _ = TestSnmpAsync(), () => !IsSnmpTesting);
    }

    // The lists are built once per settings window, in the language it was opened in.
    public IReadOnlyList<Choice> ProfileChoices { get; }
    public IReadOnlyList<Choice> LinkModeChoices { get; }
    public IReadOnlyList<Choice> SnmpChoices { get; }
    public IReadOnlyList<Choice> SnmpVersionChoices { get; }
    public IReadOnlyList<Choice> SnmpUnitChoices { get; }

    public string Id => _source.Id;

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Raise(nameof(Title)); }
    }

    public string Title => string.IsNullOrWhiteSpace(Name) ? Loc.T("Set_Unnamed") : Name.Trim();

    public string Profile
    {
        get => _profile;
        set
        {
            if (!Set(ref _profile, value)) return;
            Raise(nameof(ProfileNote));
            Raise(nameof(Wg67Hint));
        }
    }

    public string ProfileNote => Loc.T(RadioProfile.Get(Profile).Tested ? "Profile_NoteTested" : "Profile_NoteUntested");

    public string LinkMode
    {
        get => _linkMode;
        set
        {
            if (!Set(ref _linkMode, value)) return;
            Raise(nameof(ShowRx));
            Raise(nameof(ShowTx));
            Raise(nameof(TxLabel));
            Raise(nameof(TxHostLabel));
            Raise(nameof(TxUserLabel));
            Raise(nameof(SnmpTxHostLabel));
            Raise(nameof(ShowTxOid));
        }
    }

    public bool ShowRx => LinkMode != nameof(RadioLinkMode.Transceiver);
    public bool ShowTx => LinkMode != nameof(RadioLinkMode.RxOnly);
    public string TxLabel => LinkMode == nameof(RadioLinkMode.Transceiver) ? "Radio" : "TX";
    public string TxHostLabel => Loc.T(ShowRx ? "Set_TxAddress" : "Set_RadioAddress");
    public string TxUserLabel => Loc.T(ShowRx ? "Set_TxUser" : "Set_RadioUser");
    public string SnmpTxHostLabel => Loc.T(ShowRx ? "Set_SnmpTxHost" : "Set_SnmpRadioHost");

    /// <summary>A separate object for the transmitter only makes sense for a receiver/transmitter pair.</summary>
    public bool ShowTxOid => ShowRx && ShowTx;

    public string RxHost { get => _rxHost; set => Set(ref _rxHost, value); }
    public string TxHost { get => _txHost; set => Set(ref _txHost, value); }
    public string Port { get => _port; set => Set(ref _port, value); }
    public string RxUser { get => _rxUser; set => Set(ref _rxUser, value); }
    public string TxUser { get => _txUser; set => Set(ref _txUser, value); }
    public string Frequency { get => _frequency; set => Set(ref _frequency, value); }

    public int RxVolume
    {
        get => _rxVolume;
        set
        {
            if (!Set(ref _rxVolume, Math.Clamp(value, 0, 200))) return;
            Raise(nameof(VolumeText));
            Raise(nameof(VolumeLabel));
        }
    }

    public string VolumeText => $"{RxVolume} %";
    public string VolumeLabel => Loc.T("Set_Volume", RxVolume);

    public int RxPan
    {
        get => _rxPan;
        set
        {
            if (!Set(ref _rxPan, Math.Clamp(value, -100, 100))) return;
            Raise(nameof(PanText));
            Raise(nameof(BalanceLabel));
        }
    }

    public string PanText => RxPan == 0 ? Loc.T("Pan_Centre") : RxPan < 0 ? Loc.T("Pan_Left", -RxPan) : Loc.T("Pan_Right", RxPan);
    public string BalanceLabel => Loc.T("Set_Balance", PanText);

    public string Snmp { get => _snmp; set => Set(ref _snmp, value); }
    public string SnmpCommunity { get => _snmpCommunity; set => Set(ref _snmpCommunity, value); }
    public string SnmpPort { get => _snmpPort; set => Set(ref _snmpPort, value); }
    public string SnmpVersion { get => _snmpVersion; set => Set(ref _snmpVersion, value); }
    public string SnmpUnit { get => _snmpUnit; set => Set(ref _snmpUnit, value); }
    public string SnmpFrequencyOid { get => _snmpFrequencyOid; set => Set(ref _snmpFrequencyOid, value); }
    public string SnmpTxFrequencyOid { get => _snmpTxFrequencyOid; set => Set(ref _snmpTxFrequencyOid, value); }
    public string SnmpRxHost { get => _snmpRxHost; set => Set(ref _snmpRxHost, value); }
    public string SnmpTxHost { get => _snmpTxHost; set => Set(ref _snmpTxHost, value); }

    public RelayCommand TestSnmpCommand { get; }

    /// <summary>What the last "Read now" returned, one line per unit.</summary>
    public string SnmpTestResult
    {
        get => _snmpTestResult;
        private set => Set(ref _snmpTestResult, value);
    }

    public bool IsSnmpTesting
    {
        get => _isSnmpTesting;
        private set
        {
            if (!Set(ref _isSnmpTesting, value)) return;
            TestSnmpCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Reads the frequency with the values as entered, without saving them.</summary>
    public async Task TestSnmpAsync()
    {
        if (IsSnmpTesting) return;
        if (Validate() is { } error)
        {
            SnmpTestResult = error;
            return;
        }

        var site = ToConfig();
        var target = SnmpTarget.For(site);
        if (!site.HasFrequencyControl() || target.Endpoints.Count == 0)
        {
            SnmpTestResult = Loc.T("Snmp_TestNoTarget");
            return;
        }

        IsSnmpTesting = true;
        SnmpTestResult = Loc.T("Set_SnmpTesting");
        try
        {
            var results = await new SnmpFrequencyClient().GetAllAsync(target, CancellationToken.None);
            SnmpTestResult = string.Join(Environment.NewLine, results.Select(static r => r.Success
                ? Loc.T("Snmp_TestOk", r.Host, (r.FrequencyKhz / 1000.0).ToString("F3", CultureInfo.InvariantCulture))
                : $"{r.Host}: {r.Message}"));
        }
        catch (Exception ex)
        {
            SnmpTestResult = ex.Message;
        }
        finally
        {
            IsSnmpTesting = false;
        }
    }

    public string Wg67Version { get => _wg67Version; set => Set(ref _wg67Version, value); }
    public string Wg67Hint => RadioProfile.Get(Profile).Wg67Version is { } version
        ? Loc.T("Wg67_EmptyValue", version)
        : Loc.T("Wg67_EmptyNone");
    public string User { get => _user; set => Set(ref _user, value); }
    public string Pass { get => _pass; set => Set(ref _pass, value); }

    /// <returns>null when the entry is usable; otherwise what must be corrected.</returns>
    public string? Validate()
    {
        var name = Title;
        if (string.IsNullOrWhiteSpace(Name)) return Loc.T("Val_NameMissing");
        if (!TryParsePort(Port, out _)) return Loc.T("Val_SipPort", name);
        if (ShowRx && !IsValidHost(RxHost)) return Loc.T("Val_RxAddress", name);
        if (ShowTx && !IsValidHost(TxHost)) return Loc.T(ShowRx ? "Val_TxAddress" : "Val_RadioAddress", name);
        if (!string.IsNullOrWhiteSpace(Frequency) &&
            !SiteViewModel.TryParseFrequencyKhz(Frequency, out _, out _, out var error))
            return Loc.T("Val_Frequency", name, error);
        if (!TryParsePort(SnmpPort, out _)) return Loc.T("Val_SnmpPort", name);
        if ((!string.IsNullOrWhiteSpace(SnmpFrequencyOid) && !IsValidOid(SnmpFrequencyOid)) ||
            (!string.IsNullOrWhiteSpace(SnmpTxFrequencyOid) && !IsValidOid(SnmpTxFrequencyOid)))
            return Loc.T("Val_SnmpOid", name);
        if ((!string.IsNullOrWhiteSpace(SnmpRxHost) && !IsValidHost(SnmpRxHost)) ||
            (!string.IsNullOrWhiteSpace(SnmpTxHost) && !IsValidHost(SnmpTxHost)))
            return Loc.T("Val_SnmpHost", name);
        return null;
    }

    public SiteConfig ToConfig()
    {
        var site = _source.Clone();
        site.Name = Name.Trim();
        site.Profile = RadioProfile.Get(Profile).Id;
        site.LinkMode = RadioProfile.ParseLinkMode(LinkMode).ToString();
        // The legacy single-host field is folded into the explicit addresses.
        site.SipHost = "";
        site.RxHost = ShowRx ? SiteViewModel.NormalizeHost(RxHost) : "";
        site.TxHost = ShowTx ? SiteViewModel.NormalizeHost(TxHost) : "";
        site.SipPort = TryParsePort(Port, out var port) ? port : 5060;
        site.RxUser = RxUser.Trim();
        site.TxUser = TxUser.Trim();
        site.Frequency = SiteViewModel.TryParseFrequencyKhz(Frequency, out _, out var normalized, out _) ? normalized : "";
        site.RxVolume = RxVolume;
        site.RxPan = RxPan;
        site.SnmpEnabled = Snmp switch { "On" => true, "Off" => false, _ => null };
        site.SnmpCommunity = string.IsNullOrWhiteSpace(SnmpCommunity) ? "public" : SnmpCommunity.Trim();
        site.SnmpPort = TryParsePort(SnmpPort, out var snmpPort) ? snmpPort : 161;
        site.SnmpVersion = SnmpTarget.NormalizeVersion(SnmpVersion);
        site.SnmpFrequencyUnit = SnmpTarget.UnitName(SnmpTarget.ParseUnit(SnmpUnit));
        site.SnmpFrequencyOid = SnmpFrequencyOid.Trim().TrimStart('.');
        site.SnmpTxFrequencyOid = ShowTxOid ? SnmpTxFrequencyOid.Trim().TrimStart('.') : "";
        site.SnmpRxHost = ShowRx ? SiteViewModel.NormalizeHost(SnmpRxHost) : "";
        site.SnmpTxHost = ShowTx ? SiteViewModel.NormalizeHost(SnmpTxHost) : "";
        site.Wg67Version = Wg67Version.Trim();
        site.User = string.IsNullOrWhiteSpace(User) ? null : User.Trim();
        site.Pass = string.IsNullOrEmpty(Pass) ? null : Pass;
        return site;
    }

    private static bool TryParsePort(string? text, out int port) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private static bool IsValidHost(string? text)
    {
        var host = SiteViewModel.NormalizeHost(text);
        return host.Length > 0 && (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns);
    }

    private static bool IsValidOid(string text)
    {
        var parts = text.Trim().TrimStart('.').Split('.');
        return parts.Length >= 2 && parts.All(static p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }
}
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>First entry of both device lists.</summary>
    public static string DefaultDeviceLabel => Loc.T("Audio_DefaultDevice");
    public const int MinPinLength = 4;

    private readonly AppConfig _orig;
    private readonly string _defaultDevice = DefaultDeviceLabel;
    private RadioSettingsViewModel? _selectedRadio;
    private string _errorText = "";

    public SettingsViewModel(AppConfig current)
    {
        _orig = current;

        foreach (var site in current.Radios)
            Radios.Add(new RadioSettingsViewModel(site));
        _selectedRadio = Radios.FirstOrDefault();

        // PTT
        _pttKey = string.IsNullOrWhiteSpace(current.Ptt.PttKey) ? new PttConfig().PttKey : current.Ptt.PttKey;
        _suppressKey = current.Ptt.SuppressKey;
        _singleTxAtATime = current.Ptt.SingleTxAtATime;
        _muteRxWhileTx = current.Ptt.MuteRxWhileTx;
        _arbitration = PttConfig.NormalizeArbitration(current.Ptt.Arbitration);
        _maxTxSeconds = current.Ptt.MaxTxSeconds.ToString(CultureInfo.InvariantCulture);

        // Audio
        _vad = current.Audio.Vad;
        _noiseReduction = Math.Clamp(current.Audio.NoiseReduction, 0, 2);
        _txLeadMs = current.Audio.TxLeadMs.ToString(CultureInfo.InvariantCulture);
        _txTailMs = current.Audio.TxTailMs.ToString(CultureInfo.InvariantCulture);
        InputDevices = BuildDeviceList(AudioDevices.GetInputNames(), current.Audio.InputDevice);
        OutputDevices = BuildDeviceList(AudioDevices.GetOutputNames(), current.Audio.OutputDevice);
        _inputDevice = string.IsNullOrWhiteSpace(current.Audio.InputDevice) ? _defaultDevice : current.Audio.InputDevice;
        _outputDevice = string.IsNullOrWhiteSpace(current.Audio.OutputDevice) ? _defaultDevice : current.Audio.OutputDevice;

        // General
        _uiMode = current.Ui.Mode == UiConfig.Grid ? UiConfig.Grid : UiConfig.Classic;
        _language = Loc.Languages.Any(l => string.Equals(l.Code, current.Ui.Language?.Trim(), StringComparison.OrdinalIgnoreCase))
            ? current.Ui.Language!.Trim().ToLowerInvariant()
            : "";
        _alwaysOnTop = current.Ui.AlwaysOnTop;
        _organization = current.Ui.Organization ?? "";
        _autoReconnect = current.Connection.AutoReconnect;
        _connectOnStartup = current.Connection.ConnectOnStartup;

        AddRadioCommand = new RelayCommand(AddRadio, () => CanAddRadio);
        RemoveRadioCommand = new RelayCommand(RemoveRadio, () => CanRemoveRadio);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => Move(1), () => CanMove(1));
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    // Dialog control
    public Action<bool?>? RequestClose { get; set; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddRadioCommand { get; }
    public RelayCommand RemoveRadioCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Shown at the bottom of the window.</summary>
    public string ConfigPath { get; init; } = "";

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    // ---------------------------------------------------------------- radios

    public ObservableCollection<RadioSettingsViewModel> Radios { get; } = new();

    public RadioSettingsViewModel? SelectedRadio
    {
        get => _selectedRadio;
        set
        {
            if (!Set(ref _selectedRadio, value)) return;
            RaiseRadioCommands();
        }
    }

    public bool CanAddRadio => Radios.Count < AppConfig.MaxRadios;
    public bool CanRemoveRadio => Radios.Count > 1 && SelectedRadio != null;
    public string RadioCountText => $"{Radios.Count} of {AppConfig.MaxRadios}";
    public string RadiosHeader => Loc.T("Set_RadiosInOrder", Radios.Count, AppConfig.MaxRadios);

    private void AddRadio()
    {
        if (!CanAddRadio) return;
        var number = Radios.Count + 1;
        while (Radios.Any(r => string.Equals(r.Name.Trim(), $"RADIO {number}", StringComparison.OrdinalIgnoreCase))) number++;

        // New radios start from the profile and layout of the selected one; addresses stay empty.
        var template = SelectedRadio;
        var radio = new RadioSettingsViewModel(new SiteConfig
        {
            Id = "R" + Guid.NewGuid().ToString("N")[..8],
            Name = $"RADIO {number}",
            Profile = template?.Profile ?? RadioProfile.BasicId,
            LinkMode = template?.LinkMode ?? nameof(RadioLinkMode.Separate)
        });
        Radios.Add(radio);
        SelectedRadio = radio;
        RaiseRadioCommands();
    }

    private void RemoveRadio()
    {
        if (!CanRemoveRadio || SelectedRadio is not { } radio) return;
        var index = Radios.IndexOf(radio);
        Radios.Remove(radio);
        SelectedRadio = Radios[Math.Min(index, Radios.Count - 1)];
        RaiseRadioCommands();
    }

    private bool CanMove(int direction)
    {
        if (SelectedRadio is not { } radio) return false;
        var target = Radios.IndexOf(radio) + direction;
        return target >= 0 && target < Radios.Count;
    }

    private void Move(int direction)
    {
        if (!CanMove(direction) || SelectedRadio is not { } radio) return;
        var index = Radios.IndexOf(radio);
        Radios.Move(index, index + direction);
        RaiseRadioCommands();
    }

    private void RaiseRadioCommands()
    {
        Raise(nameof(CanAddRadio));
        Raise(nameof(CanRemoveRadio));
        Raise(nameof(RadioCountText));
        Raise(nameof(RadiosHeader));
        AddRadioCommand.RaiseCanExecuteChanged();
        RemoveRadioCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------- PTT

    public IReadOnlyList<Choice> ArbitrationChoices { get; } = new[]
    {
        new Choice(PttConfig.FirstWins, Loc.T("Arb_First")),
        new Choice(PttConfig.LastWins, Loc.T("Arb_Last")),
        new Choice(PttConfig.PriorityFirst, Loc.T("Arb_Priority"))
    };

    public List<string> ArbitrationOptions { get; } = new() { PttConfig.FirstWins, PttConfig.LastWins, PttConfig.PriorityFirst };

    private string _pttKey;
    public string PttKey { get => _pttKey; set => Set(ref _pttKey, value); }

    private bool _suppressKey;
    public bool SuppressKey { get => _suppressKey; set => Set(ref _suppressKey, value); }

    private bool _singleTxAtATime;
    public bool SingleTxAtATime { get => _singleTxAtATime; set => Set(ref _singleTxAtATime, value); }

    private bool _muteRxWhileTx;
    public bool MuteRxWhileTx { get => _muteRxWhileTx; set => Set(ref _muteRxWhileTx, value); }

    private string _arbitration;
    public string Arbitration { get => _arbitration; set => Set(ref _arbitration, value); }

    private string _maxTxSeconds;
    public string MaxTxSeconds { get => _maxTxSeconds; set => Set(ref _maxTxSeconds, value); }

    // ---------------------------------------------------------------- audio

    public List<string> InputDevices { get; }
    public List<string> OutputDevices { get; }

    private string _inputDevice;
    public string InputDevice { get => _inputDevice; set => Set(ref _inputDevice, value); }

    private string _outputDevice;
    public string OutputDevice { get => _outputDevice; set => Set(ref _outputDevice, value); }

    private bool _vad;
    public bool Vad { get => _vad; set => Set(ref _vad, value); }

    public IReadOnlyList<LevelChoice> NoiseReductionChoices { get; } = new[]
    {
        new LevelChoice(0, Loc.T("Nr_Off")),
        new LevelChoice(1, Loc.T("Nr_Normal")),
        new LevelChoice(2, Loc.T("Nr_Strong"))
    };

    private int _noiseReduction;
    public int NoiseReduction { get => _noiseReduction; set => Set(ref _noiseReduction, value); }

    private string _txLeadMs;
    public string TxLeadMs { get => _txLeadMs; set => Set(ref _txLeadMs, value); }

    private string _txTailMs;
    public string TxTailMs { get => _txTailMs; set => Set(ref _txTailMs, value); }

    private static bool TryParseTiming(string? text, out int ms) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ms) && ms <= AudioConfig.MaxTxTimingMs;

    private List<string> BuildDeviceList(IEnumerable<string> present, string? saved)
    {
        var list = new List<string> { _defaultDevice };
        list.AddRange(present.Where(static n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase));
        // Keep a saved device that is unplugged right now instead of silently dropping it.
        if (!string.IsNullOrWhiteSpace(saved) && !list.Contains(saved, StringComparer.OrdinalIgnoreCase))
            list.Add(saved);
        return list;
    }

    // ---------------------------------------------------------------- general

    public IReadOnlyList<Choice> UiModeChoices { get; } = new[]
    {
        new Choice(UiConfig.Classic, Loc.T("View_Classic")),
        new Choice(UiConfig.Grid, Loc.T("View_Grid"))
    };

    /// <summary>Empty value = follow the language of Windows. The names are shown in their own language.</summary>
    public IReadOnlyList<Choice> LanguageChoices { get; } =
        new[] { new Choice("", Loc.T("Gen_LanguageAuto")) }
            .Concat(Loc.Languages.Select(static l => new Choice(l.Code, l.NativeName)))
            .ToList();

    private string _language;
    public string Language { get => _language; set => Set(ref _language, value ?? ""); }

    private string _uiMode;
    public string UiMode { get => _uiMode; set => Set(ref _uiMode, value); }

    private bool _alwaysOnTop;
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }

    private string _organization;
    public string Organization { get => _organization; set => Set(ref _organization, value); }

    private bool _autoReconnect;
    public bool AutoReconnect { get => _autoReconnect; set => Set(ref _autoReconnect, value); }

    private bool _connectOnStartup;
    public bool ConnectOnStartup { get => _connectOnStartup; set => Set(ref _connectOnStartup, value); }

    // ---------------------------------------------------------------- settings PIN

    public bool HasPin => _orig.Security.HasPin;
    public string PinStatus => Loc.T(HasPin ? "Pin_IsSet" : "Pin_NotSet");

    /// <summary>Filled from the password boxes; never stored in clear text.</summary>
    public string NewPin { get; set; } = "";
    public string NewPinRepeat { get; set; } = "";

    private bool _removePin;
    public bool RemovePin { get => _removePin; set => Set(ref _removePin, value); }

    // ---------------------------------------------------------------- save

    private void Save()
    {
        var error = Validate();
        if (error != null)
        {
            ErrorText = error;
            return;
        }

        ErrorText = "";
        RequestClose?.Invoke(true);
    }

    /// <returns>null when everything can be saved; otherwise what must be corrected.</returns>
    public string? Validate()
    {
        foreach (var radio in Radios)
        {
            if (radio.Validate() is { } error)
            {
                SelectedRadio = radio;
                return error;
            }
        }

        var duplicate = Radios.GroupBy(r => r.Title, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) return Loc.T("Val_Duplicate", duplicate.Key);

        if (!TryParseKey(PttKey, out _)) return Loc.T("Val_PttKey");
        if (!int.TryParse(MaxTxSeconds?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 3600)
            return Loc.T("Val_Limit");
        if (!TryParseTiming(TxLeadMs, out _))
            return Loc.T("Val_Lead", AudioConfig.MaxTxTimingMs);
        if (!TryParseTiming(TxTailMs, out _))
            return Loc.T("Val_Tail", AudioConfig.MaxTxTimingMs);

        if (!RemovePin && NewPin.Length > 0)
        {
            if (NewPin.Length < MinPinLength) return Loc.T("Val_PinShort", MinPinLength);
            if (NewPin != NewPinRepeat) return Loc.T("Val_PinMismatch");
        }

        return null;
    }

    public static bool TryParseKey(string? name, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            return Enum.TryParse(name.Trim(), ignoreCase: true, out key) && Enum.IsDefined(key) &&
                   key != Key.None && KeyInterop.VirtualKeyFromKey(key) != 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public AppConfig ToConfig()
    {
        var cfg = new AppConfig
        {
            ConfigVersion = AppConfig.CurrentVersion,
            Radios = Radios.Select(static r => r.ToConfig()).ToList(),
            Ptt = new PttConfig
            {
                PttKey = PttKey?.Trim() ?? "",
                SuppressKey = SuppressKey,
                SingleTxAtATime = SingleTxAtATime,
                MuteRxWhileTx = MuteRxWhileTx,
                Arbitration = PttConfig.NormalizeArbitration(Arbitration),
                MaxTxSeconds = int.TryParse(MaxTxSeconds?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    ? Math.Clamp(seconds, 0, 3600)
                    : _orig.Ptt.MaxTxSeconds
            },
            Audio = new AudioConfig
            {
                Codec = _orig.Audio.Codec,
                Vad = Vad,
                NoiseGateConfigured = true,
                NoiseReduction = Math.Clamp(NoiseReduction, 0, 2),
                TxLeadMs = TryParseTiming(TxLeadMs, out var lead) ? lead : _orig.Audio.TxLeadMs,
                TxTailMs = TryParseTiming(TxTailMs, out var tail) ? tail : _orig.Audio.TxTailMs,
                InputDevice = DeviceOrDefault(InputDevice),
                OutputDevice = DeviceOrDefault(OutputDevice)
            },
            Ui = new UiConfig
            {
                Mode = UiMode == UiConfig.Grid ? UiConfig.Grid : UiConfig.Classic,
                AlwaysOnTop = AlwaysOnTop,
                Language = Language,
                Organization = Organization?.Trim() ?? ""
            },
            Connection = new ConnectionConfig
            {
                AutoReconnect = AutoReconnect,
                ConnectOnStartup = ConnectOnStartup
            },
            Security = new SecurityConfig
            {
                PinSalt = _orig.Security.PinSalt,
                PinHash = _orig.Security.PinHash
            }
        };

        if (RemovePin) PinHasher.SetPin(cfg.Security, null);
        else if (NewPin.Length >= MinPinLength && NewPin == NewPinRepeat) PinHasher.SetPin(cfg.Security, NewPin);

        ConfigMigrations.Normalize(cfg);
        return cfg;
    }

    private string? DeviceOrDefault(string? name) =>
        string.IsNullOrWhiteSpace(name) || name == _defaultDevice ? null : name;
}
